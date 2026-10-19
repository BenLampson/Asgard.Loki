using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>人工维护 namespace 定义、独立目标金样和原生调用；不从生产转换器生成预期。</summary>
internal static class NamespacedToolFixtures
{
    /// <summary>两个选项分别控制 namespace 身份展开和 custom 文本封装。</summary>
    internal static LlmConversionOptions Options => CustomToolFixtures.Options with { EnableNamespacedToolConversion = true };
    /// <summary>根名称、双下划线拼接外观名称及两个同名 namespace 函数互相独立。</summary>
    internal static JsonObject Request() => CustomToolFixtures.Parse("""
        {"model":"model-a","max_output_tokens":128,"input":"start","tool_choice":"auto","tools":[
         {"type":"function","name":"lookup","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"type":"function","name":"crm__lookup","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"type":"namespace","name":"crm","description":"","tools":[
          {"type":"function","name":"lookup","description":"CRM","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}},
          {"type":"custom","name":"code","description":"","format":{"type":"text"}}]},
         {"type":"namespace","name":"support","description":"","tools":[
          {"type":"function","name":"lookup","description":"Support","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}]}]}
        """);
    /// <summary>三种目标工具结构分别手写；占位符仅代表本次实际映射名称。</summary>
    internal static JsonObject ExpectedRequest(LlmProtocol target) => CustomToolFixtures.Parse(target switch
    {
        LlmProtocol.ChatCompletions => """
        {"model":"model-a","max_completion_tokens":128,"messages":[{"role":"user","content":[{"type":"text","text":"start"}]}],"tool_choice":"auto","tools":[
         {"type":"function","function":{"name":"lookup","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}},
         {"type":"function","function":{"name":"crm__lookup","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}},
         {"type":"function","function":{"name":"CRM_FN","description":"CRM","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}},
         {"type":"function","function":{"name":"CRM_CODE","description":"","parameters":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}}},
         {"type":"function","function":{"name":"SUPPORT_FN","description":"Support","parameters":{"type":"object","properties":{"n":{"type":"integer"}}}}}]}
        """,
        LlmProtocol.ClaudeMessages => """
        {"model":"model-a","max_tokens":128,"messages":[{"role":"user","content":[{"type":"text","text":"start"}]}],"tool_choice":{"type":"auto"},"tools":[
         {"name":"lookup","input_schema":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"name":"crm__lookup","input_schema":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"name":"CRM_FN","description":"CRM","input_schema":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"name":"CRM_CODE","description":"","input_schema":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}},
         {"name":"SUPPORT_FN","description":"Support","input_schema":{"type":"object","properties":{"n":{"type":"integer"}}}}]}
        """,
        _ => """
        {"model":"model-a","generationConfig":{"maxOutputTokens":128},"contents":[{"role":"user","parts":[{"text":"start"}]}],"toolConfig":{"functionCallingConfig":{"mode":"AUTO"}},"tools":[{"functionDeclarations":[
         {"name":"lookup","parametersJsonSchema":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"name":"crm__lookup","parametersJsonSchema":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"name":"CRM_FN","description":"CRM","parametersJsonSchema":{"type":"object","properties":{"n":{"type":"integer"}}}},
         {"name":"CRM_CODE","description":"","parametersJsonSchema":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}},
         {"name":"SUPPORT_FN","description":"Support","parametersJsonSchema":{"type":"object","properties":{"n":{"type":"integer"}}}}]}]}
        """
    });
    /// <summary>完整响应含两个同名 namespace 函数、custom 和根作用域函数。</summary>
    internal static JsonObject Response(LlmProtocol target) => CustomToolFixtures.Parse(target switch
    {
        LlmProtocol.ChatCompletions => """
        {"id":"reply","model":"model-a","choices":[{"index":0,"message":{"role":"assistant","content":"done","tool_calls":[
         {"id":"new_crm","type":"function","function":{"name":"CRM_FN","arguments":"{\"n\":1}"}},
         {"id":"new_support","type":"function","function":{"name":"SUPPORT_FN","arguments":"{\"n\":2}"}},
         {"id":"new_code","type":"function","function":{"name":"CRM_CODE","arguments":"{\"input\":\"next\\nline\"}"}},
         {"id":"new_root","type":"function","function":{"name":"crm__lookup","arguments":"{\"n\":3}"}},
         {"id":"new_plain","type":"function","function":{"name":"lookup","arguments":"{\"n\":4}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """,
        LlmProtocol.ClaudeMessages => """
        {"id":"reply","type":"message","role":"assistant","model":"model-a","content":[{"type":"text","text":"done"},
         {"type":"tool_use","id":"new_crm","name":"CRM_FN","input":{"n":1}},
         {"type":"tool_use","id":"new_support","name":"SUPPORT_FN","input":{"n":2}},
         {"type":"tool_use","id":"new_code","name":"CRM_CODE","input":{"input":"next\nline"}},
         {"type":"tool_use","id":"new_root","name":"crm__lookup","input":{"n":3}},
         {"type":"tool_use","id":"new_plain","name":"lookup","input":{"n":4}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":3}}
        """,
        _ => """
        {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"done"},
         {"functionCall":{"id":"new_crm","name":"CRM_FN","args":{"n":1}}},
         {"functionCall":{"id":"new_support","name":"SUPPORT_FN","args":{"n":2}}},
         {"functionCall":{"id":"new_code","name":"CRM_CODE","args":{"input":"next\nline"}}},
         {"functionCall":{"id":"new_root","name":"crm__lookup","args":{"n":3}}},
         {"functionCall":{"id":"new_plain","name":"lookup","args":{"n":4}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":3,"totalTokenCount":5}}
        """
    });
    /// <summary>源身份字段在响应中独立恢复，根函数没有凭名称前缀被分配 namespace。</summary>
    internal static JsonObject ExpectedResponse() => CustomToolFixtures.Parse("""
        {"id":"reply","object":"response","created_at":0,"model":"model-a","status":"completed","output":[
         {"type":"message","id":"msg_reply_0","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]},
         {"type":"function_call","id":"fc_new_crm","call_id":"new_crm","name":"lookup","namespace":"crm","arguments":"{\"n\":1}","status":"completed"},
         {"type":"function_call","id":"fc_new_support","call_id":"new_support","name":"lookup","namespace":"support","arguments":"{\"n\":2}","status":"completed"},
         {"type":"custom_tool_call","id":"fc_new_code","call_id":"new_code","name":"code","namespace":"crm","input":"next\nline","status":"completed"},
         {"type":"function_call","id":"fc_new_root","call_id":"new_root","name":"crm__lookup","arguments":"{\"n\":3}","status":"completed"},
         {"type":"function_call","id":"fc_new_plain","call_id":"new_plain","name":"lookup","arguments":"{\"n\":4}","status":"completed"}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
        """);
    /// <summary>从目标原生定义的固定位置读取映射，不推断生成前缀。</summary>
    internal static string Name(JsonObject wire, LlmProtocol target, int index) => (target switch
    {
        LlmProtocol.ChatCompletions => wire["tools"]![index]!["function"]!["name"],
        LlmProtocol.ClaudeMessages => wire["tools"]![index]!["name"],
        _ => wire["tools"]![0]!["functionDeclarations"]![index]!["name"]
    })!.GetValue<string>();
    /// <summary>只绑定金样中的工具名称占位符。</summary>
    internal static JsonObject Bind(JsonObject sample, JsonObject wire, LlmProtocol target) => CustomToolFixtures.Parse(sample.ToJsonString()
        .Replace("CRM_FN", Name(wire, target, 2), StringComparison.Ordinal).Replace("CRM_CODE", Name(wire, target, 3), StringComparison.Ordinal)
        .Replace("SUPPORT_FN", Name(wire, target, 4), StringComparison.Ordinal));
    /// <summary>复用独立的 17 事件样本，指定 namespace custom 和另一个 namespace 普通函数。</summary>
    internal static JsonObject[] Stream(JsonObject wire, LlmProtocol target) => CustomToolStreamFixtures.Source(target, Name(wire, target, 3))
        .Select(e => CustomToolFixtures.Parse(e.ToJsonString().Replace("\"plain\"", "\"" + Name(wire, target, 4) + "\"", StringComparison.Ordinal))).ToArray();
    /// <summary>只包含两个 namespace 普通函数的独立原生流，参数不经过 custom 包装。</summary>
    internal static JsonObject[] FunctionStream(JsonObject wire, LlmProtocol target)
    {
        var source = CustomToolStreamFixtures.Source(target, Name(wire, target, 2))
            .Select(e => CustomToolFixtures.Parse(e.ToJsonString().Replace("\"plain\"", "\"" + Name(wire, target, 3) + "\"", StringComparison.Ordinal))).ToArray();
        CustomToolStreamTests.SetArguments(source, target, "{\"n\":7}"); return source;
    }
    /// <summary>完整普通函数响应由独立原生样本绑定名称和 JSON 参数，不调用转换实现。</summary>
    internal static JsonObject FunctionResponse(JsonObject wire, LlmProtocol target)
    {
        var native = CustomToolFixtures.Bind(CustomToolFixtures.Response(target), Name(wire, target, 2));
        native = CustomToolFixtures.Parse(native.ToJsonString().Replace("\"plain\"", "\"" + Name(wire, target, 3) + "\"", StringComparison.Ordinal));
        CustomToolConversionTests.SetArguments(native, target, "{\"n\":7}"); return native;
    }
    /// <summary>为独立事件金样补入原始作用域身份，事件序列及参数仍由人工样本决定。</summary>
    internal static JsonObject[] ExpectedStream()
    {
        var events = CustomToolStreamFixtures.Expected();
        foreach (var e in events.Where(e => e["item"] is JsonObject)) Restore(e["item"]!.AsObject());
        foreach (var item in events[^1]["response"]!["output"]!.AsArray()) Restore(item!.AsObject());
        return events;
        void Restore(JsonObject item)
        {
            if (item["call_id"]?.GetValue<string>() == "new_custom") item["namespace"] = "crm";
            if (item["call_id"]?.GetValue<string>() == "new_plain") { item["namespace"] = "support"; item["name"] = "lookup"; }
        }
    }
}
