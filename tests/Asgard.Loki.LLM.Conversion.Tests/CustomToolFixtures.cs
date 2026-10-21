using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>人工编写 custom 工具协议样本；仅替换名称占位符，不使用生产转换器生成预期结果。</summary>
internal static class CustomToolFixtures
{
    /// <summary>显式开启可逆文本封装。</summary>
    internal static LlmConversionOptions Options => new() { EnableCustomToolConversion = true, DefaultMaxTokens = 128 };
    /// <summary>并列的自由文本工具与普通函数，保留原始名称和强制选择。</summary>
    internal static JsonObject Request() => Parse("""
        {"model":"model-a","max_output_tokens":128,"input":"start",
         "tools":[{"type":"custom","name":"code","description":"","format":{"type":"text"}},
          {"type":"function","name":"plain","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}],
         "tool_choice":{"type":"custom","name":"code"}}
        """);
    /// <summary>三种原生目标各自独立的请求金样。</summary>
    internal static JsonObject ExpectedRequest(LlmProtocol target) => Parse(target switch
    {
        LlmProtocol.ChatCompletions => """
        {"model":"model-a","max_completion_tokens":128,"messages":[{"role":"user","content":[{"type":"text","text":"start"}]}],
         "tools":[{"type":"function","function":{"name":"ENCODED","description":"","parameters":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}}},
          {"type":"function","function":{"name":"plain","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}}],
         "tool_choice":{"type":"function","function":{"name":"ENCODED"}}}
        """,
        LlmProtocol.ClaudeMessages => """
        {"model":"model-a","max_tokens":128,"messages":[{"role":"user","content":[{"type":"text","text":"start"}]}],
         "tools":[{"name":"ENCODED","description":"","input_schema":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}},
          {"name":"plain","input_schema":{"type":"object","properties":{"n":{"type":"integer"}}}}],
         "tool_choice":{"type":"tool","name":"ENCODED"}}
        """,
        _ => """
        {"model":"model-a","generationConfig":{"maxOutputTokens":128},"contents":[{"role":"user","parts":[{"text":"start"}]}],
         "tools":[{"functionDeclarations":[{"name":"ENCODED","description":"","parametersJsonSchema":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}},
          {"name":"plain","parametersJsonSchema":{"type":"object","properties":{"n":{"type":"integer"}}}}]}],
         "toolConfig":{"functionCallingConfig":{"mode":"ANY","allowedFunctionNames":["ENCODED"]}}}
        """
    });
    /// <summary>完整响应含文本、custom 编码函数及普通函数，互不混淆。</summary>
    internal static JsonObject Response(LlmProtocol target) => Parse(target switch
    {
        LlmProtocol.ChatCompletions => """
        {"id":"reply","object":"chat.completion","model":"model-a","choices":[{"index":0,"message":{"role":"assistant","content":"done","tool_calls":[
         {"id":"new_custom","type":"function","function":{"name":"ENCODED","arguments":"{\"input\":\"next\\nline\"}"}},
         {"id":"new_plain","type":"function","function":{"name":"plain","arguments":"{\"n\":1}"}}]},"finish_reason":"tool_calls"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """,
        LlmProtocol.ClaudeMessages => """
        {"id":"reply","type":"message","role":"assistant","model":"model-a","content":[{"type":"text","text":"done"},
         {"type":"tool_use","id":"new_custom","name":"ENCODED","input":{"input":"next\nline"}},
         {"type":"tool_use","id":"new_plain","name":"plain","input":{"n":1}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":3}}
        """,
        _ => """
        {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"done"},
         {"functionCall":{"id":"new_custom","name":"ENCODED","args":{"input":"next\nline"}}},
         {"functionCall":{"id":"new_plain","name":"plain","args":{"n":1}}}]},"finishReason":"STOP"}],
         "usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":3,"totalTokenCount":5}}
        """
    });
    /// <summary>还原后必须是 Responses custom 原生结构，普通参数和顺序保持独立。</summary>
    internal static JsonObject ExpectedResponse() => Parse("""
        {"id":"reply","object":"response","created_at":0,"model":"model-a","status":"completed","output":[
         {"type":"message","id":"msg_reply_0","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]},
         {"type":"custom_tool_call","id":"fc_new_custom","call_id":"new_custom","name":"code","input":"next\nline","status":"completed"},
         {"type":"function_call","id":"fc_new_plain","call_id":"new_plain","name":"plain","arguments":"{\"n\":1}","status":"completed"}],
         "usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
        """);
    /// <summary>读取实际编码名，不从生成规则猜测名称。</summary>
    internal static string Name(JsonObject wire, LlmProtocol target) => (target switch
    {
        LlmProtocol.ChatCompletions => wire["tools"]![0]!["function"]!["name"],
        LlmProtocol.ClaudeMessages => wire["tools"]![0]!["name"],
        _ => wire["tools"]![0]!["functionDeclarations"]![0]!["name"]
    })!.GetValue<string>();
    /// <summary>金样中的占位符不出现在用户文本，仅用于独立样本绑定本次随机名称。</summary>
    internal static JsonObject Bind(JsonObject value, string name) => Parse(value.ToJsonString().Replace("ENCODED", name, StringComparison.Ordinal));
    /// <summary>统一解析人工 JSON。</summary>
    internal static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
