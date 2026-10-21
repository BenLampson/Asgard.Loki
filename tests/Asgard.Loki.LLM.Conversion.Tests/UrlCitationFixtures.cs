using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立编写的完整引用报文与有效 Responses 引用流，不调用生产转换器生成预期值。</summary>
internal static class UrlCitationFixtures
{
    /// <summary>每次读取独立实例，避免用例之间共享可变原生 JSON。</summary>
    internal static JsonObject Read(string key)
    {
        using var stream = typeof(UrlCitationFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.citations.url-citations.json") ?? throw new InvalidDataException("引用样本缺失。");
        return JsonNode.Parse(stream)![key]!.AsObject();
    }

    /// <summary>原生引用事件及三个完成快照使用同一独立来源，事件序列显式指定。</summary>
    internal static JsonObject[] Stream() => JsonNode.Parse("""
    [
      {"type":"response.created","sequence_number":0,"response":{"id":"reply","model":"model-a","status":"in_progress","output":[]}},
      {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"in_progress","content":[]}},
      {"type":"response.content_part.added","sequence_number":2,"output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}},
      {"type":"response.output_text.delta","sequence_number":3,"output_index":0,"item_id":"m1","content_index":0,"delta":"done"},
      {"type":"response.output_text.done","sequence_number":4,"output_index":0,"item_id":"m1","content_index":0,"text":"done"},
      {"type":"response.output_text.annotation.added","sequence_number":5,"output_index":0,"item_id":"m1","content_index":0,"annotation_index":0,"annotation":{"type":"url_citation","url":"https://source.invalid/","title":"source","start_index":0,"end_index":4}},
      {"type":"response.content_part.done","sequence_number":6,"output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"done","annotations":[{"type":"url_citation","url":"https://source.invalid/","title":"source","start_index":0,"end_index":4}]}},
      {"type":"response.output_item.done","sequence_number":7,"output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[{"type":"url_citation","url":"https://source.invalid/","title":"source","start_index":0,"end_index":4}]}]}},
      {"type":"response.completed","sequence_number":8,"response":{"id":"reply","model":"model-a","status":"completed","output":[{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[{"type":"url_citation","url":"https://source.invalid/","title":"source","start_index":0,"end_index":4}]}]}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}
    ]
    """)!.AsArray().Select(e => e!.AsObject()).ToArray();

    /// <summary>两个方向的原生协议枚举。</summary>
    internal static LlmProtocol Other(LlmProtocol source) => source == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses;

    /// <summary>定位原生引用数组，不能用输出转换器来计算断言目标。</summary>
    internal static JsonArray Annotations(JsonObject response, LlmProtocol protocol) => (protocol == LlmProtocol.Responses
        ? response["output"]![0]!["content"]![0]!["annotations"] : response["choices"]![0]!["message"]!["annotations"])!.AsArray();

    /// <summary>定位原生字段，保留 Chat 嵌套引用和 Responses 平铺引用的区别。</summary>
    internal static JsonObject Citation(JsonObject response, LlmProtocol protocol, int index = 0)
    {
        var annotation = Annotations(response, protocol)[index]!;
        return (protocol == LlmProtocol.ChatCompletions ? annotation["url_citation"] : annotation)!.AsObject();
    }
}
