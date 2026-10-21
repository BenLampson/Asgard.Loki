using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>人工原生事件与独立 Responses 目标事件金样，不调用生产转换器构造预期。</summary>
internal static class CustomToolStreamFixtures
{
    /// <summary>文本、分段 custom 包装、普通函数和独立 usage/终态。</summary>
    internal static JsonObject[] Source(LlmProtocol protocol, string name) => Read((protocol switch
    {
        LlmProtocol.ChatCompletions => """
        [
         {"id":"reply","model":"model-a","choices":[{"index":0,"delta":{"role":"assistant","content":"done"},"finish_reason":null}]},
         {"id":"reply","model":"model-a","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"new_custom","type":"function","function":{"name":"ENCODED","arguments":"{\"input\":\"next"}}]}}]},
         {"id":"reply","model":"model-a","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\\nline\"}"}},{"index":1,"id":"new_plain","type":"function","function":{"name":"plain","arguments":"{\"n\":1}"}}]}}]},
         {"id":"reply","model":"model-a","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]},
         {"id":"reply","model":"model-a","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        ]
        """,
        LlmProtocol.ClaudeMessages => """
        [
         {"type":"message_start","message":{"id":"reply","type":"message","role":"assistant","model":"model-a","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":0}}},
         {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"done"}},
         {"type":"content_block_stop","index":0},
         {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"new_custom","name":"ENCODED","input":{}}},
         {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"input\":\"next"}},
         {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\\nline\"}"}},
         {"type":"content_block_stop","index":1},
         {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"new_plain","name":"plain","input":{"n":1}}},
         {"type":"content_block_stop","index":2},
         {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":3}},
         {"type":"message_stop"}
        ]
        """,
        _ => """
        [
         {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"done"}]}}]},
         {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"new_custom","name":"ENCODED","args":{"input":"next\nline"}}},{"functionCall":{"id":"new_plain","name":"plain","args":{"n":1}}}]}}]},
         {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":3,"totalTokenCount":5}}
        ]
        """
    }).Replace("ENCODED", name, StringComparison.Ordinal));

    /// <summary>目标事件的序号、索引、项目身份、文本输入、普通函数和最终快照均完整比较。</summary>
    internal static JsonObject[] Expected() => Read("""
        [
         {"type":"response.created","sequence_number":0,"response":{"id":"reply","object":"response","created_at":0,"model":"model-a","status":"in_progress","output":[]}},
         {"type":"response.in_progress","sequence_number":1,"response":{"id":"reply","object":"response","created_at":0,"model":"model-a","status":"in_progress","output":[]}},
         {"type":"response.output_item.added","output_index":0,"sequence_number":2,"item":{"id":"item_reply_0","type":"message","role":"assistant","status":"in_progress","content":[]}},
         {"type":"response.content_part.added","output_index":0,"sequence_number":3,"item_id":"item_reply_0","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}},
         {"type":"response.output_text.delta","output_index":0,"sequence_number":4,"item_id":"item_reply_0","content_index":0,"delta":"done"},
         {"type":"response.output_item.added","output_index":1,"sequence_number":5,"item":{"id":"item_reply_1","type":"custom_tool_call","call_id":"new_custom","name":"code","input":"","status":"in_progress"}},
         {"type":"response.custom_tool_call_input.delta","output_index":1,"sequence_number":6,"item_id":"item_reply_1","delta":"next\nline"},
         {"type":"response.output_item.added","output_index":2,"sequence_number":7,"item":{"id":"item_reply_2","type":"function_call","call_id":"new_plain","name":"plain","arguments":"","status":"in_progress"}},
         {"type":"response.function_call_arguments.delta","output_index":2,"sequence_number":8,"item_id":"item_reply_2","delta":"{\"n\":1}"},
         {"type":"response.output_text.done","output_index":0,"sequence_number":9,"item_id":"item_reply_0","content_index":0,"text":"done"},
         {"type":"response.content_part.done","output_index":0,"sequence_number":10,"item_id":"item_reply_0","content_index":0,"part":{"type":"output_text","text":"done","annotations":[]}},
         {"type":"response.output_item.done","output_index":0,"sequence_number":11,"item":{"id":"item_reply_0","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]}},
         {"type":"response.custom_tool_call_input.done","output_index":1,"sequence_number":12,"item_id":"item_reply_1","input":"next\nline"},
         {"type":"response.output_item.done","output_index":1,"sequence_number":13,"item":{"id":"item_reply_1","type":"custom_tool_call","call_id":"new_custom","name":"code","input":"next\nline","status":"completed"}},
         {"type":"response.function_call_arguments.done","output_index":2,"sequence_number":14,"item_id":"item_reply_2","arguments":"{\"n\":1}"},
         {"type":"response.output_item.done","output_index":2,"sequence_number":15,"item":{"id":"item_reply_2","type":"function_call","call_id":"new_plain","name":"plain","arguments":"{\"n\":1}","status":"completed"}},
         {"type":"response.completed","sequence_number":16,"response":{"id":"reply","object":"response","created_at":0,"model":"model-a","status":"completed","output":[
          {"id":"item_reply_0","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]},
          {"id":"item_reply_1","type":"custom_tool_call","call_id":"new_custom","name":"code","input":"next\nline","status":"completed"},
          {"id":"item_reply_2","type":"function_call","call_id":"new_plain","name":"plain","arguments":"{\"n\":1}","status":"completed"}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}
        ]
        """);
    /// <summary>保持独立 JSON 样本的所有事件字段。</summary>
    private static JsonObject[] Read(string json) => JsonNode.Parse(json)!.AsArray().Select(item => item!.AsObject()).ToArray();
}
