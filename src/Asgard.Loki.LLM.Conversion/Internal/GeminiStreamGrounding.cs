using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>单次 Gemini 流的累计来源/支持校验；只按调用者显式声明的单文本约定映射。</summary>
internal sealed class GeminiStreamGrounding(int maximumCount, int maximumBytes)
{
    private readonly List<JsonObject> _chunks = [];
    private readonly List<JsonObject> _supports = [];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private int _count;
    private long _bytes;
    private int _highestReference = -1;
    private bool _incompatibleLayout;
    /// <summary>先追加本帧的新来源，保留顺序和重复 URL；引用索引属于全部响应，不按帧重置。</summary>
    internal void Add(JsonNode? value, ConversionDocument? mapping = null)
    {
        if (value is not JsonObject metadata) throw new InvalidDataException("原生 groundingMetadata 必须为对象。");
        if (mapping is not null)
            foreach (var field in metadata)
                if (field.Key is not ("groundingChunks" or "groundingSupports"))
                    Loss(mapping, "$event.candidates[].groundingMetadata." + field.Key, "grounding_metadata_field_not_representable");
        foreach (var node in Values(metadata, "groundingChunks"))
        {
            var source = Item(node);
            if (new[] { "web", "image", "retrievedContext", "maps" }.Count(source.ContainsKey) > 1)
                throw new InvalidDataException("原生 grounding 来源违反 oneof 约束。");
            Store(_chunks, source);
        }
        foreach (var node in Values(metadata, "groundingSupports"))
        {
            var support = Item(node);
            foreach (var index in Values(support, "groundingChunkIndices")) _highestReference = Math.Max(_highestReference, Index(index));
            if (support.ContainsKey("segment"))
            {
                var segment = Item(support["segment"]); _ = OptionalIndex(segment, "partIndex");
                var start = OptionalIndex(segment, "startIndex"); var end = OptionalIndex(segment, "endIndex");
                if (end < start) throw new InvalidDataException("原生 grounding segment 终点早于起点。");
                if (segment.ContainsKey("text") && (segment["text"] is not JsonValue text || !text.TryGetValue<string>(out _)))
                    throw new InvalidDataException("原生 grounding segment.text 必须为字符串。");
            }
            foreach (var index in Values(support, "renderedParts")) _ = Index(index);
            Store(_supports, support);
        }
        if (mapping is not null) RequireLayout();
    }
    /// <summary>只接受每帧至多一个纯文本 part；出现多 part/其他内容时绝不猜测全局 part 身份。</summary>
    internal void ObserveContent(JsonObject content)
    {
        var parts = Values(content, "parts");
        if (parts.Count == 0) return;
        _incompatibleLayout |= parts.Count != 1 || parts[0] is not JsonObject part || !part.ContainsKey("text") || part.Any(field => field.Key != "text");
        RequireLayout();
    }
    /// <summary>正常源结束后复用完整引用映射器，逐范围验证累计原文；文本增量此前已经即时公开。</summary>
    internal void Map(ConversionMessage message, ConversionDocument document)
    {
        if (_count == 0) return;
        RequireLayout();
        if (message.Blocks.Count != 1 || Text(message.Blocks[0]["type"]) != "text") Layout();
        if (_supports.Any(support => support["renderedParts"] is JsonArray { Count: > 0 })) Layout();
        var parts = new JsonArray(new JsonObject { ["text"] = Text(message.Blocks[0]["text"]) });
        var metadata = new JsonObject
        {
            ["groundingChunks"] = new JsonArray(_chunks.Select(item => (JsonNode?)item.DeepClone()).ToArray()),
            ["groundingSupports"] = new JsonArray(_supports.Select(item => (JsonNode?)item.DeepClone()).ToArray())
        };
        GeminiGrounding.Read(metadata, parts, message, document, maximumCount, maximumBytes);
    }
    /// <summary>仅有普通文本/工具流而没有 grounding 数据时，不扩大该配置的限制范围。</summary>
    private void RequireLayout() { if (_count != 0 && _incompatibleLayout) Layout(); }
    /// <summary>布局语义无法保真时硬失败，普通损耗许可也不能选错引用所属 part。</summary>
    private static void Layout() => throw new LlmConversionException([new("grounding_stream_layout_not_representable", "$event.candidates[].content",
        "已声明的单文本 grounding 流出现无法证明所属关系的 part/工具布局。")]);
    /// <summary>允许尚待后帧来源补全的稀疏引用，但正常结束时必须全部解析到累计来源；不按最大索引分配数组。</summary>
    internal void Complete()
    {
        if (_highestReference >= _chunks.Count) throw new InvalidDataException("原生 groundingChunkIndices 未关联到累计来源表。");
    }
    /// <summary>来源和支持共享单次流条目/字节额度，深拷贝保留未知原文；普通损耗许可不能绕过。</summary>
    private void Store(List<JsonObject> destination, JsonObject value)
    {
        if (_count >= maximumCount) Limit("stream_grounding_count_limit");
        var bytes = StrictUtf8.GetByteCount(value.ToJsonString());
        if (_bytes + bytes > maximumBytes) Limit("stream_grounding_byte_limit");
        destination.Add(value.DeepClone().AsObject()); _count++; _bytes += bytes;
    }
    /// <summary>省略的 Proto3 重复字段为空；显式非法容器不被当作省略。</summary>
    private static JsonArray Values(JsonObject value, string name)
        => value.ContainsKey(name) ? value[name] as JsonArray ?? throw new InvalidDataException("原生 grounding 重复字段必须为数组。") : [];
    /// <summary>原生条目必须为对象，不能通过忽略 null/标量来移动来源索引。</summary>
    private static JsonObject Item(JsonNode? value) => value as JsonObject ?? throw new InvalidDataException("原生 grounding 条目必须为对象。");
    /// <summary>Proto3 非 optional int32 省略为零；显式 null/负数/小数均不修复。</summary>
    private static int OptionalIndex(JsonObject value, string name) => value.ContainsKey(name) ? Index(value[name]) : 0;
    /// <summary>仅验证 int32 索引，不根据文本或来源 URL 猜测另一个索引。</summary>
    private static int Index(JsonNode? value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var index) || index < 0)
            throw new InvalidDataException("原生 grounding 索引必须为非负 int32。");
        return index;
    }
    /// <summary>只报告资源边界，不将原生正文或用户内容写入错误信息。</summary>
    private static void Limit(string code) => throw new LlmConversionException([new(code, "$event.candidates[].groundingMetadata",
        "原生 grounding 状态超过调用者配置的单次流资源上限。")]);
}
