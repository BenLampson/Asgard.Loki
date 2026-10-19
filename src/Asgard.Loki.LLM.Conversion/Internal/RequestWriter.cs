using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>输出目标原生请求；转换所需默认值只接受显式配置。</summary>
internal static class RequestWriter
{
    /// <summary>写入消息、工具和可等价映射的生成配置。</summary>
    internal static JsonObject Write(ConversionDocument doc, LlmProtocol protocol, LlmConversionOptions options)
    {
        var target = new JsonObject { ["model"] = doc.Model };
        var messages = Messages(doc, protocol);
        target[protocol == LlmProtocol.Gemini ? "contents" : protocol == LlmProtocol.Responses ? "input" : "messages"] = messages;
        var config = protocol == LlmProtocol.Gemini ? new JsonObject() : target;
        foreach (var setting in doc.Settings)
        {
            var key = setting.Key switch
            {
                "temperature" => "temperature",
                "topP" => protocol == LlmProtocol.Gemini ? "topP" : "top_p",
                "topK" => protocol == LlmProtocol.Gemini ? "topK" : protocol == LlmProtocol.ClaudeMessages ? "top_k" : null,
                "maxTokens" => protocol switch { LlmProtocol.Gemini => "maxOutputTokens", LlmProtocol.Responses => "max_output_tokens", LlmProtocol.ClaudeMessages => "max_tokens", _ => "max_completion_tokens" },
                "stop" => protocol switch { LlmProtocol.Gemini => "stopSequences", LlmProtocol.ClaudeMessages => "stop_sequences", LlmProtocol.ChatCompletions => "stop", _ => null },
                _ => null
            };
            if (key is null)
            {
                if (setting.Key != "parallel") Loss(doc, "$settings." + setting.Key);
            }
            else config[key] = setting.Key == "stop" && protocol != LlmProtocol.ChatCompletions && setting.Value is JsonValue
                ? new JsonArray(setting.Value.DeepClone()) : setting.Value?.DeepClone();
        }
        if (protocol == LlmProtocol.Gemini && config.Count != 0) target["generationConfig"] = config;
        if (protocol == LlmProtocol.ClaudeMessages && !target.ContainsKey("max_tokens"))
            target["max_tokens"] = options.DefaultMaxTokens ?? throw new ArgumentException("转换为 Claude 必须显式提供输出上限或 DefaultMaxTokens。");
        if (protocol == LlmProtocol.ClaudeMessages && (target["max_tokens"] is null || Number(target["max_tokens"]) <= 0 || Number(target["max_tokens"]) > int.MaxValue))
            throw new ArgumentException("Claude 输出上限必须为正整数。");
        var systems = doc.Messages.Where(m => m.Role is "system" or "developer").ToArray();
        if (protocol is LlmProtocol.Gemini or LlmProtocol.ClaudeMessages
            && doc.Messages.SkipWhile(m => m.Role is "system" or "developer").Any(m => m.Role is "system" or "developer"))
            Loss(doc, "$messages[].role", "instruction_order_changed");
        if (protocol is LlmProtocol.Gemini or LlmProtocol.ClaudeMessages && systems.Length != 0)
        {
            if (systems.Any(m => m.Role == "developer")) Loss(doc, "$messages[].role", "role_semantics_changed");
            var blocks = new JsonArray();
            foreach (var system in systems)
                foreach (var block in system.Blocks)
                {
                    if (Text(block["type"]) != "text") { Loss(doc, "$system", "unsupported_content"); continue; }
                    blocks.Add(protocol == LlmProtocol.Gemini ? new JsonObject { ["text"] = Text(block["text"]) }
                        : new JsonObject { ["type"] = "text", ["text"] = Text(block["text"]) });
                }
            target[protocol == LlmProtocol.Gemini ? "systemInstruction" : "system"] = protocol == LlmProtocol.Gemini ? new JsonObject { ["parts"] = blocks } : blocks;
        }
        ToolMapping.Write(target, protocol, doc, options);
        WriteParallel(target, protocol, doc);
        ReasoningMapping.Write(target, protocol, doc, options);
        StructuredOutputMapping.Write(target, protocol, doc, options);
        return target;
    }

    /// <summary>按内容顺序产生目标消息；工具结果始终通过原始关联 ID 匹配。</summary>
    internal static JsonArray Messages(ConversionDocument doc, LlmProtocol protocol)
    {
        var result = new JsonArray();
        var calls = new ToolCallTracker();
        foreach (var message in doc.Messages)
        {
            if (message.Role is not ("system" or "developer" or "user" or "assistant" or "tool"))
                throw new ArgumentException("不支持此消息角色。");
            if (message.Blocks.Any(b => Text(b["type"]) == "call" && message.Role != "assistant"
                || Text(b["type"]) == "result" && message.Role is not ("user" or "tool")))
                throw new ArgumentException("工具调用或结果所在的消息角色无效。");
            if (message.Role is "system" or "developer" && protocol is LlmProtocol.ClaudeMessages or LlmProtocol.Gemini) continue;
            var role = protocol == LlmProtocol.Gemini && message.Role == "assistant" ? "model" : message.Role;
            if (protocol == LlmProtocol.ChatCompletions && role == "assistant" && calls.HasPending
                && (message.Blocks.Any(b => Text(b["type"]) != "call") || result.LastOrDefault() is not JsonObject prior || prior["tool_calls"] is not JsonArray))
                RejectChatToolOrder();
            if (protocol == LlmProtocol.Responses)
            {
                JsonArray content = [];
                void Flush()
                {
                    if (content.Count == 0) return;
                    result.Add(new JsonObject { ["type"] = "message", ["role"] = role, ["content"] = content }); content = [];
                }
                foreach (var block in message.Blocks)
                {
                    var type = Text(block["type"]);
                    if (type == "text") content.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = Text(block["text"]) });
                    else if (type == "image") content.Add(ContentMapping.Image(block, protocol, doc));
                    else if (type == "file") content.Add(InputFileMapping.Write(block, protocol, doc));
                    else
                    {
                        Flush();
                        var id = Text(block["id"]);
                        if (type == "call") { Register(calls, block); result.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = Text(block["name"]), ["arguments"] = Text(block["arguments"]) }); }
                        else { RequireCall(calls, id); result.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = ToolResultMapping.Write(block, protocol, doc) }); }
                    }
                }
                Flush();
                continue;
            }
            JsonArray parts = [], toolCalls = [];
            foreach (var block in message.Blocks)
            {
                var type = Text(block["type"]);
                if (protocol == LlmProtocol.ChatCompletions && role != "assistant" && calls.HasPending && type != "result") RejectChatToolOrder();
                if (type == "text")
                {
                    if (protocol == LlmProtocol.ChatCompletions && toolCalls.Count != 0) Loss(doc, "$messages[].content", "block_order_changed");
                    parts.Add(protocol == LlmProtocol.Gemini ? new JsonObject { ["text"] = Text(block["text"]) }
                        : new JsonObject { ["type"] = "text", ["text"] = Text(block["text"]) });
                }
                else if (type == "image") parts.Add(ContentMapping.Image(block, protocol, doc));
                else if (type == "file") parts.Add(InputFileMapping.Write(block, protocol, doc));
                else if (type == "call")
                {
                    Register(calls, block);
                    var id = Text(block["id"]); var name = Text(block["name"]); var arguments = Text(block["arguments"]);
                    if (protocol == LlmProtocol.ChatCompletions) toolCalls.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } });
                    else
                    {
                        var args = Object(JsonNode.Parse(arguments));
                        parts.Add(protocol == LlmProtocol.Gemini ? new JsonObject { ["functionCall"] = new JsonObject { ["id"] = id, ["name"] = name, ["args"] = args.DeepClone() } }
                            : new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = args.DeepClone() });
                    }
                }
                else
                {
                    var id = Text(block["id"]); var name = RequireCall(calls, id); var output = ToolResultMapping.Write(block, protocol, doc);
                    if (protocol == LlmProtocol.ChatCompletions)
                    {
                        if (parts.Count != 0) { result.Add(new JsonObject { ["role"] = role, ["content"] = parts }); parts = []; }
                        result.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output });
                    }
                    else if (protocol == LlmProtocol.Gemini)
                    {
                        var response = JsonNode.Parse(Text(output)) as JsonObject;
                        if (response is null) throw new LlmConversionException([new("tool_result_object_required", "$tool_result", "Gemini 工具结果必须是 JSON 对象，SDK 不隐式包装文本。")]);
                        parts.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["id"] = id, ["name"] = name, ["response"] = response } });
                    }
                    else parts.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output });
                    role = "user";
                }
            }
            if (parts.Count == 0 && toolCalls.Count == 0) continue;
            if (protocol == LlmProtocol.ChatCompletions && role == "assistant" && parts.Count == 0 && toolCalls.Count != 0
                && result.LastOrDefault() is JsonObject previous && previous["role"]?.GetValue<string>() == "assistant" && previous["tool_calls"] is JsonArray previousCalls)
            {
                // 连续 Responses function_call 属于同一并行工具轮次，Chat 必须先完整声明再逐 ID 回填结果。
                foreach (var call in toolCalls) previousCalls.Add(call!.DeepClone());
                continue;
            }
            var item = new JsonObject { ["role"] = role == "tool" ? "user" : role, [protocol == LlmProtocol.Gemini ? "parts" : "content"] = parts };
            if (toolCalls.Count != 0) item["tool_calls"] = toolCalls;
            result.Add(item);
        }
        return result;
    }

    /// <summary>不能通过重排、丢弃消息或拆开未回填的工具轮次制造一个表面合法的 Chat 请求。</summary>
    private static void RejectChatToolOrder() => throw new LlmConversionException([new("tool_turn_order_not_representable", "$messages", "Chat 工具轮次尚未完整回填，不能插入其他消息或提前开始下一轮调用。")]);

    /// <summary>拒绝重复调用 ID，避免错误关联。</summary>
    private static void Register(ToolCallTracker calls, JsonObject block)
    {
        _ = Object(JsonNode.Parse(Text(block["arguments"])));
        ArgumentException.ThrowIfNullOrWhiteSpace(Text(block["id"]));
        ArgumentException.ThrowIfNullOrWhiteSpace(Text(block["name"]));
        calls.Register(Text(block["id"]), Text(block["name"]));
    }
    /// <summary>拒绝无对应函数调用的结果。</summary>
    private static string RequireCall(ToolCallTracker calls, string id) => calls.Consume(id);

    /// <summary>显式并行工具开关使用目标原生字段；Gemini 没有可等价表达的开关。</summary>
    private static void WriteParallel(JsonObject target, LlmProtocol protocol, ConversionDocument doc)
    {
        if (!doc.Settings.ContainsKey("parallel")) return;
        var parallel = Boolean(doc.Settings["parallel"]);
        if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses) target["parallel_tool_calls"] = parallel;
        else if (protocol == LlmProtocol.ClaudeMessages)
        {
            if (doc.Tools.Count == 0) { Loss(doc, "$settings.parallel", "parallel_tools_required"); return; }
            if (target["tool_choice"] is not JsonObject) target["tool_choice"] = new JsonObject { ["type"] = "auto" };
            if (target["tool_choice"]!["type"]?.GetValue<string>() == "none") { Loss(doc, "$settings.parallel", "parallel_tools_disabled"); return; }
            target["tool_choice"]!["disable_parallel_tool_use"] = !parallel;
        }
        else Loss(doc, "$settings.parallel", "unsupported_parallel_tools");
    }
}
