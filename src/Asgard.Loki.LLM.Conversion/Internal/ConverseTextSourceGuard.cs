using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>工具身份和内容联合分支不能因普通扩展损耗许可而被读取器跳过。</summary>
internal static class ConverseTextSourceGuard
{
    /// <summary>在规范化之前拒绝错位的工具身份、非普通调用和未实现的执行内容。</summary>
    internal static void Validate(JsonObject source, LlmProtocol protocol)
    {
        if (protocol == LlmProtocol.ClaudeMessages && source["thinking"] is JsonObject thinking)
        {
            if (Text(thinking["type"]) is not ("enabled" or "adaptive" or "disabled"))
                BedrockConversion.Reject("$.thinking.type", "unsupported_thinking_mode");
            if (thinking.ContainsKey("display")) _ = Text(thinking["display"]);
        }
        if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.ClaudeMessages)
        foreach (var node in Array(source["messages"]))
        {
            var message = Object(node); var role = Text(message["role"]);
            if (protocol == LlmProtocol.ChatCompletions)
            {
                if (message.ContainsKey("tool_call_id") && role != "tool" || message.ContainsKey("tool_calls") && role != "assistant")
                    throw new ArgumentException("工具关联字段必须位于原生对应角色，不能按普通扩展丢弃。");
                if (message["tool_calls"] is JsonArray calls)
                    foreach (var call in calls)
                        if (Text(Object(call)["type"]) != "function") BedrockConversion.Reject("$.messages[].tool_calls[]", "converse_tool_not_representable");
            }
            if (message["content"] is JsonArray blocks)
            foreach (var block in blocks)
            {
                var type = Text(Object(block)["type"]);
                var allowed = protocol == LlmProtocol.ChatCompletions ? type is "text" or "image_url" : type is "text" or "image" or "tool_use" or "tool_result";
                if (!allowed) BedrockConversion.Reject("$.messages[].content[]", "converse_content_not_representable");
            }
        }
        if (protocol == LlmProtocol.Responses && source["input"] is JsonArray items)
        foreach (var node in items)
        {
            var item = Object(node); var type = item["type"]?.GetValue<string>() ?? "message";
            if (type is not ("message" or "function_call" or "function_call_output"))
                BedrockConversion.Reject("$.input[]", "converse_content_not_representable");
        }
    }
}
