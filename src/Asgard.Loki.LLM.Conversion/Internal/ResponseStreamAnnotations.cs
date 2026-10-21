using System.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>有界保存原生引用身份和完成快照；允许显示损耗不等于允许源引用不一致。</summary>
internal sealed class ResponseStreamAnnotations(int maximumCount, int maximumBytes)
{
    private readonly Dictionary<string, SortedDictionary<int, JsonNode?>> _parts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private int _count;
    private long _bytes;

    /// <summary>保存开始快照中的引用；后续事件不能重新占用已经存在的原生索引。</summary>
    internal void Start(string key, JsonObject part, ConversionDocument document)
    {
        var annotations = Values(part);
        for (var i = 0; i < annotations.Count; i++) Store(key, i, annotations[i], document);
    }

    /// <summary>引用事件可晚于 text.done，也可按原生索引乱序到达，但不能重复或在 part.done 后新增。</summary>
    internal void Add(string key, int index, JsonNode? value, ConversionDocument document)
    {
        if (_closed.Contains(key)) throw new InvalidDataException("已完成内容块不能新增引用。");
        Store(key, index, value, document);
    }

    /// <summary>完成快照可首次提供完整引用，但已经观察到的条目不能消失、换序或改变任何原生字段。</summary>
    internal void Snapshot(string key, JsonObject part, ConversionDocument document)
    {
        var values = Values(part); var previous = Part(key);
        if (_closed.Contains(key) && values.Count != previous.Count) throw new InvalidDataException("引用完成快照的条目数量发生变化。");
        foreach (var (index, value) in previous)
            if (index >= values.Count || !JsonNode.DeepEquals(value, values[index]))
                throw new InvalidDataException("引用完成快照与原生 annotation_index 或已观察到的数据不一致。");
        if (_closed.Contains(key)) return;
        for (var i = 0; i < values.Count; i++)
        {
            if (!previous.ContainsKey(i)) Store(key, i, values[i], document);
            if (values[i] is JsonObject annotation && annotation["type"]?.GetValue<string>() == "url_citation")
                _ = ResponseCitations.Read(new JsonArray(annotation.DeepClone()), Text(part["text"]), LlmProtocol.Responses, document, "$event.part.annotations");
        }
        _closed.Add(key);
    }

    /// <summary>输出项和最终响应的每个原生文本 part 都要与此前引用状态匹配，不能只比较纯文本。</summary>
    internal void Item(int outputIndex, JsonObject item, ConversionDocument document)
    {
        if (Text(item["type"]) != "message") return;
        var parts = Array(item["content"]);
        for (var i = 0; i < parts.Count; i++)
            if (parts[i] is JsonObject part && part["type"]?.GetValue<string>() == "output_text")
                Snapshot("responses-" + outputIndex + "-text-" + i, part, document);
    }

    /// <summary>只累计唯一原生条目的资源占用，重复完成快照不累加；超限必须失败，不能通过 AllowLoss 丢弃。</summary>
    private void Store(string key, int index, JsonNode? value, ConversionDocument document)
    {
        if (value is not null and not JsonObject) throw new InvalidDataException("原生 annotation 必须是对象或 null。");
        if (value is JsonObject typed && typed.ContainsKey("type") && (typed["type"] is not JsonValue type || !type.TryGetValue<string>(out _)))
            throw new InvalidDataException("原生 annotation.type 必须是字符串。");
        var part = Part(key);
        if (part.ContainsKey(index)) throw new InvalidDataException("同一内容块的 annotation_index 重复。");
        if (_count >= maximumCount) Limit("stream_annotation_count_limit");
        var bytes = StrictUtf8.GetByteCount(value?.ToJsonString() ?? "null");
        if (_bytes + bytes > maximumBytes) Limit("stream_annotation_byte_limit");
        part.Add(index, value?.DeepClone()); _count++; _bytes += bytes;
        if (value is not JsonObject annotation || annotation["type"]?.GetValue<string>() != "url_citation")
            Loss(document, "$event.annotation", "annotation_not_representable");
    }
    /// <summary>空引用不分配稀疏索引数组；原始 annotation_index 只作为字典键保存。</summary>
    private SortedDictionary<int, JsonNode?> Part(string key)
    {
        if (!_parts.TryGetValue(key, out var value)) _parts.Add(key, value = []);
        return value;
    }
    /// <summary>省略视为空列表；显式 null 或非数组不能冒充原生 annotations。</summary>
    private static JsonArray Values(JsonObject part)
        => part.ContainsKey("annotations") ? part["annotations"] as JsonArray ?? throw new InvalidDataException("原生 annotations 必须是数组。") : [];
    /// <summary>资源边界是硬失败，不包含上游正文或调用者隐私。</summary>
    private static void Limit(string code)
        => throw new LlmConversionException([new(code, "$event.annotation", "原生引用状态超过调用者配置的单次流资源上限。")]);
}
