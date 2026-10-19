using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立编写的原生搜索声明和完整目标报文；文本回复仅验证协议，不冒充搜索效果。</summary>
internal static class HostedSearchFixtures
{
    /// <summary>声明当前固定目标；模型/账号是否接受仍由调用者验证。</summary>
    internal static LlmConversionOptions Options(LlmProtocol target) => new()
    {
        DefaultMaxTokens = 128,
        HostedSearchTarget = target switch
        {
            LlmProtocol.Responses => LlmHostedSearchTarget.ResponsesWebSearch,
            LlmProtocol.ClaudeMessages => LlmHostedSearchTarget.ClaudeWebSearch20250305,
            LlmProtocol.Gemini => LlmHostedSearchTarget.GeminiGoogleSearch,
            _ => LlmHostedSearchTarget.None
        }
    };
    /// <summary>三种原生启用结构，没有伪造函数或自动查询参数。</summary>
    internal static JsonObject Request(LlmProtocol source) => CustomToolFixtures.Parse(source switch
    {
        LlmProtocol.Responses => """{"model":"model-a","max_output_tokens":128,"input":"start","tools":[{"type":"web_search"}]}""",
        LlmProtocol.ClaudeMessages => """{"model":"model-a","max_tokens":128,"messages":[{"role":"user","content":"start"}],"tools":[{"type":"web_search_20250305","name":"web_search"}]}""",
        _ => """{"model":"model-a","generationConfig":{"maxOutputTokens":128},"contents":[{"role":"user","parts":[{"text":"start"}]}],"tools":[{"googleSearch":{}}]}"""
    });
    /// <summary>完整目标金样独立维护，包含消息、输出上限和实际原生搜索工具。</summary>
    internal static JsonObject Expected(LlmProtocol target) => CustomToolFixtures.Parse(target switch
    {
        LlmProtocol.Responses => """{"model":"model-a","max_output_tokens":128,"input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"start"}]}],"tools":[{"type":"web_search"}]}""",
        LlmProtocol.ClaudeMessages => """{"model":"model-a","max_tokens":128,"messages":[{"role":"user","content":[{"type":"text","text":"start"}]}],"tools":[{"type":"web_search_20250305","name":"web_search"}]}""",
        _ => """{"model":"model-a","generationConfig":{"maxOutputTokens":128},"contents":[{"role":"user","parts":[{"text":"start"}]}],"tools":[{"googleSearch":{}}]}"""
    });
    /// <summary>不含搜索执行/引用的合法文本响应，不能据此宣称真实搜索或完整引用转换通过。</summary>
    internal static JsonObject TextResponse(LlmProtocol target) => CustomToolFixtures.Parse(target switch
    {
        LlmProtocol.ChatCompletions => """{"id":"reply","model":"model-a","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}""",
        LlmProtocol.Responses => """
        {"id":"reply","object":"response","model":"model-a","status":"completed","output":[{"id":"m1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
        """,
        LlmProtocol.ClaudeMessages => """
        {"id":"reply","type":"message","model":"model-a","role":"assistant","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":3}}
        """,
        _ => """
        {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"done"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":3,"totalTokenCount":5}}
        """
    });
    /// <summary>独立有效的文本 SSE，不带搜索结果以明确区分请求配置和结果能力。</summary>
    internal static JsonObject[] TextStream(LlmProtocol target) => JsonNode.Parse(target switch
    {
        LlmProtocol.Responses => """
        [
         {"type":"response.created","sequence_number":0,"response":{"id":"reply","model":"model-a","status":"in_progress","output":[]}},
         {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"in_progress","content":[]}},
         {"type":"response.content_part.added","sequence_number":2,"output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}},
         {"type":"response.output_text.delta","sequence_number":3,"output_index":0,"item_id":"m1","content_index":0,"delta":"done"},
         {"type":"response.output_text.done","sequence_number":4,"output_index":0,"item_id":"m1","content_index":0,"text":"done"},
         {"type":"response.content_part.done","sequence_number":5,"output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"done","annotations":[]}},
         {"type":"response.output_item.done","sequence_number":6,"output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]}},
         {"type":"response.completed","sequence_number":7,"response":{"id":"reply","model":"model-a","status":"completed","output":[{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"done","annotations":[]}]}],"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}
        ]
        """,
        LlmProtocol.ClaudeMessages => """
        [
         {"type":"message_start","message":{"id":"reply","type":"message","role":"assistant","model":"model-a","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2,"output_tokens":0}}},
         {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},
         {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}},
         {"type":"content_block_stop","index":0},
         {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":3}},
         {"type":"message_stop"}
        ]
        """,
        _ => """
        [
         {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"done"}]}}]},
         {"responseId":"reply","modelVersion":"model-a","candidates":[{"index":0,"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":3,"totalTokenCount":5}}
        ]
        """
    })!.AsArray().Select(e => e!.AsObject()).ToArray();
    /// <summary>原生加密搜索结果不是可跨协议替换的显示文本。</summary>
    internal static JsonObject EncryptedResult() => CustomToolFixtures.Parse("""{"type":"web_search_tool_result","tool_use_id":"search1","content":[{"type":"web_search_result","url":"https://source.invalid","title":"source","encrypted_content":"opaque"}]}""");
}
