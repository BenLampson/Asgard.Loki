using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立 Responses 原生样本和手算 Gemini 字节范围，不从生产转换器捕获预期。</summary>
internal static class ResponseCitationStreamFixtures
{
    /// <summary>每次返回独立对象，避免跨用例修改样本。</summary>
    internal static JsonNode Read(string key)
    {
        using var stream = typeof(ResponseCitationStreamFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.grounding.responses-to-gemini-stream.json") ?? throw new InvalidDataException("引用流样本缺失。");
        return JsonNode.Parse(stream)![key]!;
    }
    /// <summary>三种手算原生单位产生相同 Gemini 字节范围，所有快照保持一致。</summary>
    internal static List<JsonObject> Source(LlmCitationIndexUnit unit = LlmCitationIndexUnit.UnicodeScalar)
    {
        var source = Read("Source").AsArray().Select(node => node!.AsObject()).ToList();
        if (unit == LlmCitationIndexUnit.UnicodeScalar) return source;
        var annotations = Read(unit == LlmCitationIndexUnit.Utf16CodeUnit ? "Utf16Annotations" : "Utf8Annotations").AsArray();
        for (var i = 0; i < 4; i++) source[7 + i]["annotation"] = annotations[i]!.DeepClone();
        source[12]["part"]!["annotations"] = annotations.DeepClone(); source[13]["item"]!["content"]![0]!["annotations"] = annotations.DeepClone();
        source[14]["response"]!["output"]![0]!["content"]![0]!["annotations"] = annotations.DeepClone(); return source;
    }
    /// <summary>共享显式单文本约定只用于已声明的 Responses/Gemini 方向。</summary>
    internal static LlmConversionOptions Options(LlmCitationIndexUnit unit = LlmCitationIndexUnit.UnicodeScalar)
        => GroundingCitationStreamFixtures.Options(unit);
    /// <summary>完整源成功后才调用 Complete，不模拟 HTTP 成功或补终态。</summary>
    internal static List<LlmConversionResult> Run(IEnumerable<JsonObject> source, LlmConversionOptions? options = null)
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.Gemini, options ?? Options());
        List<LlmConversionResult> events = []; foreach (var item in source) events.AddRange(converter.Push(item)); events.AddRange(converter.Complete()); return events;
    }
    /// <summary>同一原生引用的各快照保持一致，用于隔离字段本身而不是先触发快照不一致。</summary>
    internal static void ChangeFirstAnnotation(List<JsonObject> source, Action<JsonObject> change)
    {
        change(source[7]["annotation"]!.AsObject()); change(source[12]["part"]!["annotations"]![0]!.AsObject());
        change(source[13]["item"]!["content"]![0]!["annotations"]![0]!.AsObject()); change(source[14]["response"]!["output"]![0]!["content"]![0]!["annotations"]![0]!.AsObject());
    }
    /// <summary>为布局负例追加合法第二文本或工具项，最终响应与所有增量保持相符。</summary>
    internal static void AddSecondItem(List<JsonObject> source, bool tool)
    {
        JsonObject final;
        if (tool)
        {
            final = JsonNode.Parse("""{"id":"tool-item","type":"function_call","call_id":"call-original","name":"lookup","arguments":"{\"id\":9007199254740993}","status":"completed"}""")!.AsObject();
            var start = final.DeepClone().AsObject(); start["arguments"] = ""; start["status"] = "in_progress";
            source.InsertRange(source.Count - 1, [new() { ["type"] = "response.output_item.added", ["output_index"] = 1, ["item"] = start },
                new() { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 1, ["item_id"] = "tool-item", ["delta"] = "{\"id\":9007199254740993}" },
                new() { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = final.DeepClone() }]);
        }
        else
        {
            final = JsonNode.Parse("""{"id":"second","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"other","annotations":[]}]}""")!.AsObject();
            var start = final.DeepClone().AsObject(); start["status"] = "in_progress"; start["content"] = new JsonArray();
            source.InsertRange(source.Count - 1, [new() { ["type"] = "response.output_item.added", ["output_index"] = 1, ["item"] = start },
                new() { ["type"] = "response.content_part.added", ["output_index"] = 1, ["content_index"] = 0, ["item_id"] = "second", ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() } },
                new() { ["type"] = "response.output_text.delta", ["output_index"] = 1, ["content_index"] = 0, ["item_id"] = "second", ["delta"] = "other" },
                new() { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = final.DeepClone() }]);
        }
        source[^1]["response"]!["output"]!.AsArray().Add(final.DeepClone()); for (var i = 0; i < source.Count; i++) source[i]["sequence_number"] = i;
    }
}
