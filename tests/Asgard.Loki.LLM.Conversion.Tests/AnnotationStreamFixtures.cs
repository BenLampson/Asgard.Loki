using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>手工编写的多 part 来源和完整 Chat 事件预期；只测试显式引用损耗，不伪称目标有引用 delta。</summary>
internal static class AnnotationStreamFixtures
{
    /// <summary>每次读取独立嵌入样本，不从生产转换结果生成预期。</summary>
    internal static List<JsonObject> Read(string key = "Source")
    {
        using var stream = typeof(AnnotationStreamFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.citations.stream-integrity.json") ?? throw new InvalidDataException("引用流样本缺失。");
        return JsonNode.Parse(stream)![key]!.AsArray().Select(node => node!.AsObject()).ToList();
    }
    /// <summary>明确声明这份样本使用 UTF-16 单位并允许显示引用损耗。</summary>
    internal static LlmConversionOptions Options => new() { AllowLoss = true, OpenAiCitationIndexUnit = LlmCitationIndexUnit.Utf16CodeUnit };
    /// <summary>完整运行公开状态机并收集所有事件。</summary>
    internal static List<LlmConversionResult> Run(IEnumerable<JsonObject> source, LlmConversionOptions? options = null, LlmProtocol target = LlmProtocol.ChatCompletions)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, target, options ?? Options); List<LlmConversionResult> results = [];
        foreach (var item in source) results.AddRange(converter.Push(item));
        results.AddRange(converter.Complete()); return results;
    }
    /// <summary>索引和事件次序改变后重新赋予单调序号，不让序号错误掩盖引用错误。</summary>
    internal static void Sequence(IList<JsonObject> source) { for (var i = 0; i < source.Count; i++) source[i]["sequence_number"] = i; }
    /// <summary>枚举三个完成快照的原生引用数组，不包含增量事件。</summary>
    internal static IEnumerable<JsonArray> Snapshots(IEnumerable<JsonObject> source)
    {
        foreach (var item in source)
        {
            if (item["part"] is JsonObject part && part["annotations"] is JsonArray values) yield return values;
            if (item["item"]?["content"] is JsonArray content)
                foreach (var block in content.OfType<JsonObject>()) if (block["annotations"] is JsonArray annotations) yield return annotations;
            if (item["response"]?["output"] is JsonArray output)
                foreach (var message in output.OfType<JsonObject>())
                    if (message["content"] is JsonArray blocks)
                        foreach (var block in blocks.OfType<JsonObject>()) if (block["annotations"] is JsonArray annotations) yield return annotations;
        }
    }
    /// <summary>同时修改已声明的原始引用与完成快照，避免其他不一致让范围校验用例误通过。</summary>
    internal static IEnumerable<JsonObject> Citations(IEnumerable<JsonObject> source)
    {
        foreach (var item in source)
            if (item["annotation"] is JsonObject annotation) yield return annotation;
        foreach (var list in Snapshots(source)) foreach (var annotation in list.OfType<JsonObject>()) yield return annotation;
    }
    /// <summary>建立两个不同引用的原生事件与快照，可明确验证乱序索引和跨条目的资源计数。</summary>
    internal static List<JsonObject> TwoAnnotations(bool reversed = false)
    {
        var source = Read(); var second = source.Single(item => item["type"]!.GetValue<string>() == "response.output_text.annotation.added").DeepClone().AsObject();
        second["annotation_index"] = 1; second["annotation"]!["url"] = "https://second.invalid/"; second["annotation"]!["title"] = "第二来源";
        foreach (var list in Snapshots(source).Where(list => list.Count != 0)) list.Add(second["annotation"]!.DeepClone());
        var at = source.FindIndex(item => item["type"]!.GetValue<string>() == "response.output_text.annotation.added"); source.Insert(reversed ? at : at + 1, second);
        Sequence(source); return source;
    }
}
