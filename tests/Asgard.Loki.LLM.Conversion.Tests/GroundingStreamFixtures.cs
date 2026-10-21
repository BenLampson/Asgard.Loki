using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>手工编写的增量来源/支持样本与有损目标预期，不把来源表当每帧完整快照。</summary>
internal static class GroundingStreamFixtures
{
    /// <summary>每次加载独立原生样本或手工预期。</summary>
    internal static JsonNode Read(string key)
    {
        using var stream = typeof(GroundingStreamFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.grounding.stream-integrity.json") ?? throw new InvalidDataException("grounding 流样本缺失。");
        return JsonNode.Parse(stream)![key]!;
    }
    /// <summary>原生源事件按服务器顺序排列。</summary>
    internal static List<JsonObject> Source() => Read("Source").AsArray().Select(n => n!.AsObject()).ToList();
    /// <summary>只访问原生候选位置，不扫描用户文本中的同名字段。</summary>
    internal static JsonObject Metadata(List<JsonObject> source, int at) => source[at]["candidates"]![0]!["groundingMetadata"]!.AsObject();
    /// <summary>明确允许既有显示损耗，保留未映射诊断。</summary>
    internal static LlmConversionOptions Options => new() { AllowLoss = true, DefaultMaxTokens = 64 };
    /// <summary>走公开纯转换入口，直到源确认完成和目标最终化。</summary>
    internal static List<LlmConversionResult> Run(IEnumerable<JsonObject> source, LlmConversionOptions? options = null, LlmProtocol target = LlmProtocol.ChatCompletions)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Gemini, target, options ?? Options); List<LlmConversionResult> seen = [];
        foreach (var item in source) seen.AddRange(converter.Push(item)); seen.AddRange(converter.Complete()); return seen;
    }
    /// <summary>只按被实际保留的来源/支持 JSON 条目测量额度，不把本地序列化长度冒充线上字节数。</summary>
    internal static IEnumerable<JsonNode> Items(List<JsonObject> source)
    {
        foreach (var frame in source)
            if (frame["candidates"] is JsonArray candidates)
                foreach (var candidate in candidates.OfType<JsonObject>())
                    if (candidate["groundingMetadata"] is JsonObject metadata)
                        foreach (var field in new[] { "groundingChunks", "groundingSupports" })
                            if (metadata[field] is JsonArray list) foreach (var item in list) yield return item!;
    }
    /// <summary>原生候选先给 finish，后帧仅补 grounding 来源，不重开文本或重复结束。</summary>
    internal static List<JsonObject> MetadataTail()
    {
        var source = Source(); var tail = source[3].DeepClone().AsObject(); tail["candidates"]![0]!.AsObject().Remove("finishReason");
        source[3]["candidates"]![0]!.AsObject().Remove("groundingMetadata"); source.Insert(4, tail); return source;
    }
    /// <summary>元数据尾帧不得掩盖同时到达的阻断信号，即使它是最后一帧且此前已有用量。</summary>
    internal static List<JsonObject> BlockedMetadataTail()
    {
        var source = MetadataTail(); source[3]["usageMetadata"] = source[^1]["usageMetadata"]!.DeepClone(); source.RemoveAt(source.Count - 1);
        source[^1]["promptFeedback"] = new JsonObject { ["blockReason"] = "SAFETY" }; return source;
    }
    /// <summary>用量尾帧也不得掩盖阻断信号，不能只防护新增的 grounding 分支。</summary>
    internal static List<JsonObject> BlockedUsageTail()
    {
        var source = Source(); source[^1]["promptFeedback"] = new JsonObject { ["blockReason"] = "SAFETY" }; return source;
    }
}
