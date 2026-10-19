using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>两条消息/四个文本 part 的独立原生样本与手算累计字节引用。</summary>
internal static class ConcatenatedCitationFixtures
{
    /// <summary>每次读取完整独立对象，不跨测试共享可变 DOM。</summary>
    internal static JsonNode Read(string key)
    {
        using var stream = typeof(ConcatenatedCitationFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.grounding.concatenated-stream.json") ?? throw new InvalidDataException("顺序拼接引用样本缺失。");
        return JsonNode.Parse(stream)![key]!;
    }
    /// <summary>三份完整快照显式写出原生索引单位，不使用生产换算生成预期。</summary>
    internal static List<JsonObject> Source(LlmCitationIndexUnit unit = LlmCitationIndexUnit.UnicodeScalar)
        => Read("Source" + unit).AsArray().Select(item => item!.AsObject()).ToList();
    /// <summary>只有新枚举值启用拼接，原 SingleTextPart 的边界保持不变。</summary>
    internal static LlmConversionOptions Options(LlmCitationIndexUnit unit = LlmCitationIndexUnit.UnicodeScalar)
        => ResponseCitationStreamFixtures.Options(unit) with { GeminiGroundingStreamMode = GeminiGroundingStreamMode.ConcatenatedTextParts };
    /// <summary>只有源枚举正常结束后才调用 Complete；默认不允许损耗。</summary>
    internal static List<LlmConversionResult> Run(IEnumerable<JsonObject> source, LlmConversionOptions? options = null)
        => ResponseCitationStreamFixtures.Run(source, options ?? Options());
    /// <summary>建立合法交错原生文本事件，源最终文本保持原样但实际增量不再按块拼接顺序到达。</summary>
    internal static void Interleave(List<JsonObject> source)
    {
        var first = source.Single(item => TextEvent(item, "response.output_text.delta", 0)); first["delta"] = "前";
        var done = source.Single(item => TextEvent(item, "response.output_text.done", 0)); var partDone = source.Single(item => TextEvent(item, "response.content_part.done", 0)); source.Remove(done); source.Remove(partDone);
        var after = source.FindIndex(item => TextEvent(item, "response.output_text.delta", 1)); var tail = first.DeepClone().AsObject(); tail["delta"] = "😀"; source.InsertRange(after + 1, [tail, done, partDone]); Resequence(source);
    }
    /// <summary>提前声明后续空 part 不改变实际文本到达顺序，不应被当作交错正文。</summary>
    internal static void StartNextPartEarly(List<JsonObject> source)
    {
        var start = source.Single(item => TextEvent(item, "response.content_part.added", 1)); source.Remove(start);
        var before = source.FindIndex(item => TextEvent(item, "response.output_text.delta", 0)); source.Insert(before, start); Resequence(source);
    }
    /// <summary>修改同一引用的全部原生副本，避免快照差异先于范围本身触发失败。</summary>
    internal static void ChangeCitation(List<JsonObject> source, bool last, Action<JsonObject> change)
    {
        var output = last ? 1 : 0;
        var annotation = source.Single(item => item["type"]!.GetValue<string>() == "response.output_text.annotation.added" && item["output_index"]!.GetValue<int>() == output && item["content_index"]!.GetValue<int>() == 1 && item["annotation_index"]!.GetValue<int>() == 0); change(annotation["annotation"]!.AsObject());
        var part = source.Single(item => item["type"]!.GetValue<string>() == "response.content_part.done" && item["output_index"]!.GetValue<int>() == output && item["content_index"]!.GetValue<int>() == 1); change(part["part"]!["annotations"]![0]!.AsObject());
        var item = source.Single(item => item["type"]!.GetValue<string>() == "response.output_item.done" && item["output_index"]!.GetValue<int>() == output); change(item["item"]!["content"]![1]!["annotations"]![0]!.AsObject()); change(source[^1]["response"]!["output"]![output]!["content"]![1]!["annotations"]![0]!.AsObject());
    }
    /// <summary>构造文字完全相同的合法逆序 part 开始，原生快照仍按真实 content_index 关联，排除纯文本比较的假阳性。</summary>
    internal static void ReorderIdenticalParts(List<JsonObject> source)
    {
        foreach (var item in source.Where(item => item["output_index"]?.GetValue<int>() == 0))
        {
            var type = item["type"]!.GetValue<string>(); if (item.ContainsKey("content_index")) item["content_index"] = 1 - item["content_index"]!.GetValue<int>();
            if (type == "response.output_text.delta") item["delta"] = "X";
            if (type == "response.output_text.done") item["text"] = "X";
            if (type == "response.output_text.annotation.added") item["annotation"]!["end_index"] = 1;
            if (type == "response.content_part.done") { item["part"]!["text"] = "X"; foreach (var citation in item["part"]!["annotations"]!.AsArray()) citation!["end_index"] = 1; }
            if (type == "response.output_item.done") NormalizeAndSwap(item["item"]!["content"]!.AsArray());
        }
        NormalizeAndSwap(source[^1]["response"]!["output"]![0]!["content"]!.AsArray());
    }
    /// <summary>只用于手工负例的文本/范围和原生数组顺序，不生成任何目标预期。</summary>
    private static void NormalizeAndSwap(JsonArray parts)
    {
        foreach (var part in parts) { part!["text"] = "X"; foreach (var citation in part["annotations"]!.AsArray()) citation!["end_index"] = 1; }
        var first = parts[0]!.DeepClone(); parts[0] = parts[1]!.DeepClone(); parts[1] = first;
    }
    /// <summary>已知原生位置匹配，不扫描工具参数里的同名字段。</summary>
    internal static bool TextEvent(JsonObject item, string type, int part) => item["type"]!.GetValue<string>() == type
        && item["output_index"]?.GetValue<int>() == 0 && item["content_index"]?.GetValue<int>() == part;
    /// <summary>变更事件排列时重新给出合法递增序号，避免序号错误掩盖待测语义。</summary>
    internal static void Resequence(List<JsonObject> source) { for (var i = 0; i < source.Count; i++) source[i]["sequence_number"] = i; }
}
