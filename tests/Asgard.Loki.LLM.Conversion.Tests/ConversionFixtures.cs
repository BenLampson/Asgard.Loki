using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>人工维护的协议样本，独立于生产转换实现。</summary>
internal static class ConversionFixtures
{
    /// <summary>包含系统指令、内联图片、具名工具及结果回填的四种原生请求。</summary>
    internal static JsonObject Request(LlmProtocol protocol) => JsonNode.Parse(protocol switch
    {
        LlmProtocol.ChatCompletions => """
        {"model":"model-a","max_completion_tokens":128,"temperature":0,"top_p":1,
         "messages":[{"role":"system","content":"rules"},{"role":"user","content":[{"type":"text","text":"你好"},{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}]},
          {"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"x\":0}"}}]},
          {"role":"tool","tool_call_id":"call_1","content":"{\"ok\":false}"},{"role":"user","content":"done"}],
         "tools":[{"type":"function","function":{"name":"lookup","description":"look","parameters":{"type":"object","properties":{"x":{"type":"integer"}}}}}],
         "tool_choice":{"type":"function","function":{"name":"lookup"}}}
        """,
        LlmProtocol.Responses => """
        {"model":"model-a","max_output_tokens":128,"temperature":0,"top_p":1,"instructions":"rules",
         "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"你好"},{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]},
          {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"x\":0}"},
          {"type":"function_call_output","call_id":"call_1","output":"{\"ok\":false}"},{"role":"user","content":"done"}],
         "tools":[{"type":"function","name":"lookup","description":"look","parameters":{"type":"object","properties":{"x":{"type":"integer"}}}}],
         "tool_choice":{"type":"function","name":"lookup"}}
        """,
        LlmProtocol.ClaudeMessages => """
        {"model":"model-a","max_tokens":128,"temperature":0,"top_p":1,"system":"rules",
         "messages":[{"role":"user","content":[{"type":"text","text":"你好"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"aGVsbG8="}}]},
          {"role":"assistant","content":[{"type":"tool_use","id":"call_1","name":"lookup","input":{"x":0}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"{\"ok\":false}"}]},{"role":"user","content":"done"}],
         "tools":[{"name":"lookup","description":"look","input_schema":{"type":"object","properties":{"x":{"type":"integer"}}}}],
         "tool_choice":{"type":"tool","name":"lookup"}}
        """,
        _ => """
        {"model":"model-a","generationConfig":{"maxOutputTokens":128,"temperature":0,"topP":1},"systemInstruction":{"parts":[{"text":"rules"}]},
         "contents":[{"role":"user","parts":[{"text":"你好"},{"inlineData":{"mimeType":"image/png","data":"aGVsbG8="}}]},
          {"role":"model","parts":[{"functionCall":{"id":"call_1","name":"lookup","args":{"x":0}}}]},
          {"role":"user","parts":[{"functionResponse":{"id":"call_1","name":"lookup","response":{"ok":false}}}]},{"role":"user","parts":[{"text":"done"}]}],
         "tools":[{"functionDeclarations":[{"name":"lookup","description":"look","parametersJsonSchema":{"type":"object","properties":{"x":{"type":"integer"}}}}]}],
         "toolConfig":{"functionCallingConfig":{"mode":"ANY","allowedFunctionNames":["lookup"]}}}
        """
    })!.AsObject();

    /// <summary>相同语义但各自原生结构的响应，均带稳定调用关联 ID。</summary>
    internal static JsonObject Response(LlmProtocol protocol) => JsonNode.Parse(protocol switch
    {
        LlmProtocol.ChatCompletions => """
        {"id":"r1","object":"chat.completion","model":"model-a","choices":[{"index":0,"message":{"role":"assistant","content":"你好","tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"x\":0}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
        """,
        LlmProtocol.Responses => """
        {"id":"r1","object":"response","model":"model-a","status":"completed","output":[{"type":"message","id":"msg1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"你好","annotations":[]}]},{"type":"function_call","id":"fc1","call_id":"call_1","name":"lookup","arguments":"{\"x\":0}","status":"completed"}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}
        """,
        LlmProtocol.ClaudeMessages => """
        {"id":"r1","type":"message","role":"assistant","model":"model-a","content":[{"type":"text","text":"你好"},{"type":"tool_use","id":"call_1","name":"lookup","input":{"x":0}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":2}}
        """,
        _ => """
        {"responseId":"r1","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"你好"},{"functionCall":{"id":"call_1","name":"lookup","args":{"x":0}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}
        """
    })!.AsObject();

    /// <summary>人工流事件包含两个 Unicode 文本增量和拆分函数参数。</summary>
    internal static IEnumerable<JsonObject> Stream(LlmProtocol protocol)
    {
        string[] events = protocol switch
        {
            LlmProtocol.ChatCompletions =>
            [
                """{"id":"r1","model":"model-a","choices":[{"index":0,"delta":{"role":"assistant","content":"你"},"finish_reason":null}]}""",
                """{"id":"r1","model":"model-a","choices":[{"index":0,"delta":{"content":"好"}}]}""",
                """{"id":"r1","model":"model-a","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"x\":"}}]}}]}""",
                """{"id":"r1","model":"model-a","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"0}"}}]},"finish_reason":"tool_calls"}]}""",
                """{"id":"r1","model":"model-a","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""
            ],
            LlmProtocol.ClaudeMessages =>
            [
                """{"type":"message_start","message":{"id":"r1","type":"message","role":"assistant","model":"model-a","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":0}}}""",
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你"}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"好"}}""",
                """{"type":"content_block_stop","index":0}""",
                """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call_1","name":"lookup","input":{}}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"x\":"}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"0}"}}""",
                """{"type":"content_block_stop","index":1}""",
                """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":2}}""",
                """{"type":"message_stop"}"""
            ],
            LlmProtocol.Gemini =>
            [
                """{"responseId":"r1","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"你"}]}}]}""",
                """{"responseId":"r1","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"好"}]}}]}""",
                """{"responseId":"r1","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"call_1","name":"lookup","args":{"x":0}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}"""
            ],
            _ =>
            [
                """{"type":"response.created","response":{"id":"r1","model":"model-a"}}""",
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"msg1","type":"message","role":"assistant","content":[],"status":"in_progress"}}""",
                """{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"你"}""",
                """{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"好"}""",
                """{"type":"response.output_item.added","output_index":1,"item":{"id":"fc1","type":"function_call","call_id":"call_1","name":"lookup","arguments":"","status":"in_progress"}}""",
                """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"x\":"}""",
                """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"0}"}""",
                """{"type":"response.function_call_arguments.done","output_index":1,"arguments":"{\"x\":0}"}"""
            ]
        };
        foreach (var item in events) yield return JsonNode.Parse(item)!.AsObject();
        if (protocol == LlmProtocol.Responses) yield return new() { ["type"] = "response.completed", ["response"] = Response(protocol) };
    }
}
