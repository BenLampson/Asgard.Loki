using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>单次 Gemini 流的累计原生来源表与支持引用校验，不推导文本 part 身份或目标引用事件。</summary>
internal sealed class GeminiStreamGrounding(int maximumCount, int maximumBytes)
{
    private readonly List<JsonObject> _chunks = [];
    private readonly List<JsonObject> _supports = [];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private int _count;
    private long _bytes;
    private int _highestReference = -1;
    /// <summary>先追加本帧的新来源，保留顺序和重复 URL；引用索引属于全部响应，不按帧重置。</summary>
    internal void Add(JsonNode? value)
    {
        if (value is not JsonObject metadata) throw new InvalidDataException("原生 groundingMetadata 必须为对象。");
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
    }
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
