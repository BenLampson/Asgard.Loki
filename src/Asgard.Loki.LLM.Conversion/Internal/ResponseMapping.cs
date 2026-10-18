using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>转换单个候选的完整响应，保留函数 ID 并显式诊断无法表示的数据。</summary>
internal static class ResponseMapping
{
    /// <summary>读取完整响应，不把失败响应转换为正常完成。</summary>
    internal static ConversionDocument Read(JsonObject source, LlmProtocol protocol)
    {
        var doc = new ConversionDocument
        {
            Id = source[protocol == LlmProtocol.Gemini ? "responseId" : "id"]?.GetValue<string>() ?? "loki_" + Guid.NewGuid().ToString("N"),
            Model = source[protocol == LlmProtocol.Gemini ? "modelVersion" : "model"]?.GetValue<string>() ?? "",
            Created = Number(source[protocol == LlmProtocol.ChatCompletions ? "created" : "created_at"])
        };
        var message = new ConversionMessage("assistant");
        if (source["error"] is not null) throw new LlmConversionException([new("upstream_error", "$.error", "错误响应不能转换为成功响应。")]);
        source = ResponseEnvelope.Content(source, protocol, doc);
        switch (protocol)
        {
            case LlmProtocol.ChatCompletions:
                Check(source, doc, "$", "id", "object", "created", "model", "choices", "usage");
                var choices = Array(source["choices"]);
                if (choices.Count != 1) throw new LlmConversionException([new("multiple_candidates", "$.choices", "响应转换只支持一个候选。")]);
                var choice = Object(choices[0]);
                Check(choice, doc, "$.choices[0]", "index", "message", "finish_reason", "logprobs");
                if (choice["logprobs"] is not null) Loss(doc, "$.choices[0].logprobs");
                var chat = Object(choice["message"]);
                chat = chat.DeepClone().AsObject();
                if (chat.ContainsKey("refusal") && chat["refusal"] is null) chat.Remove("refusal");
                if (chat["annotations"] is JsonArray annotations && annotations.Count == 0) chat.Remove("annotations");
                if (Text(chat["role"]) != "assistant") throw new ArgumentException("生成响应必须是 assistant 消息。");
                var request = new JsonObject { ["model"] = doc.Model, ["messages"] = new JsonArray(chat.DeepClone()) };
                var read = RequestReader.Read(request, protocol);
                message.Blocks.AddRange(read.Messages.Single().Blocks);
                doc.Diagnostics.AddRange(read.Diagnostics);
                doc.Finish = Finish(Text(choice["finish_reason"]), protocol);
                UsageMapping.Read(source["usage"], protocol, doc);
                break;
            case LlmProtocol.Responses:
                Check(source, doc, "$", "id", "object", "created_at", "model", "status", "output", "usage", "incomplete_details", "error");
                var status = Text(source["status"]);
                if (status is not ("completed" or "incomplete")) throw new InvalidDataException("Responses 未正常完成，不能转换为成功响应。");
                if (status == "completed" && source["incomplete_details"] is not null)
                    throw new InvalidDataException("已完成的 Responses 不能同时携带未完成原因。");
                doc.Finish = "stop";
                if (status == "incomplete")
                {
                    var details = Object(source["incomplete_details"]); Check(details, doc, "$.incomplete_details", "reason");
                    doc.Finish = Text(details["reason"]) switch
                    {
                        "max_output_tokens" or "length" => "length", "content_filter" => "content_filter",
                        _ => throw new LlmConversionException([new("unsupported_finish_reason", "$.incomplete_details.reason", "无法等价表达此终止原因。")])
                    };
                }
                foreach (var node in Array(source["output"]))
                {
                    var item = Object(node);
                    var type = Text(item["type"]);
                    if (type == "message")
                    {
                        Check(item, doc, "$.output[]", "type", "id", "status", "role", "content");
                        if (Text(item["role"]) != "assistant") throw new ArgumentException("生成响应必须是 assistant 消息。");
                        ContentMapping.Read(item["content"], protocol, message, doc);
                    }
                    else if (type == "function_call")
                    {
                        Check(item, doc, "$.output[]", "type", "id", "status", "call_id", "name", "arguments");
                        message.Blocks.Add(new() { ["type"] = "call", ["id"] = Text(item["call_id"]), ["name"] = Text(item["name"]), ["arguments"] = Text(item["arguments"]) });
                    }
                    else if (type is "reasoning" or "compaction") throw new LlmConversionException([new("opaque_state", "$.output[]." + type, "不透明推理或压缩项必须使用原生协议。")]);
                    else Loss(doc, "$.output[]." + type, "unsupported_item");
                }
                if (doc.Finish == "stop" && message.Blocks.Any(b => Text(b["type"]) == "call")) doc.Finish = "tool_calls";
                UsageMapping.Read(source["usage"], protocol, doc);
                break;
            case LlmProtocol.ClaudeMessages:
                Check(source, doc, "$", "id", "type", "role", "model", "content", "stop_reason", "stop_sequence", "usage");
                if (Text(source["type"]) != "message" || Text(source["role"]) != "assistant") throw new ArgumentException("Claude 生成响应类型或角色无效。");
                ContentMapping.Read(source["content"], protocol, message, doc);
                doc.Finish = Finish(Text(source["stop_reason"]), protocol);
                if (source["stop_sequence"] is not null) Loss(doc, "$.stop_sequence");
                UsageMapping.Read(source["usage"], protocol, doc);
                break;
            case LlmProtocol.Gemini:
                Check(source, doc, "$", "responseId", "modelVersion", "candidates", "usageMetadata");
                var candidates = Array(source["candidates"]);
                if (candidates.Count != 1) throw new LlmConversionException([new("multiple_candidates", "$.candidates", "响应转换只支持一个候选。")]);
                var candidate = Object(candidates[0]);
                Check(candidate, doc, "$.candidates[0]", "index", "content", "finishReason");
                var content = Object(candidate["content"]);
                Check(content, doc, "$.candidates[0].content", "role", "parts");
                if (Text(content["role"]) != "model") throw new ArgumentException("Gemini 生成响应角色必须为 model。");
                ContentMapping.Read(content["parts"], protocol, message, doc);
                doc.Finish = Finish(Text(candidate["finishReason"]), protocol);
                if (doc.Finish == "stop" && message.Blocks.Any(b => Text(b["type"]) == "call")) doc.Finish = "tool_calls";
                UsageMapping.Read(source["usageMetadata"], protocol, doc);
                break;
        }
        doc.Messages.Add(message);
        foreach (var call in message.Blocks.Where(b => Text(b["type"]) == "call"))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Text(call["name"]));
            _ = Object(JsonNode.Parse(Text(call["arguments"])));
        }
        var ids = message.Blocks.Where(b => Text(b["type"]) == "call").Select(b => Text(b["id"])).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("响应工具关联 ID 为空或重复。");
        ValidateFinish(doc);
        return doc;
    }

    /// <summary>生成完整目标响应，原始上游 JSON 由外层结果独立保存。</summary>
    internal static JsonObject Write(ConversionDocument doc, LlmProtocol protocol)
    {
        var message = doc.Messages.Single();
        var blocks = new JsonArray(); var calls = new JsonArray();
        foreach (var block in message.Blocks)
        {
            var type = Text(block["type"]);
            if (type == "text") blocks.Add(protocol switch
            {
                LlmProtocol.Gemini => new JsonObject { ["text"] = Text(block["text"]) },
                LlmProtocol.Responses => new JsonObject { ["type"] = "output_text", ["text"] = Text(block["text"]), ["annotations"] = new JsonArray() },
                _ => new JsonObject { ["type"] = "text", ["text"] = Text(block["text"]) }
            });
            else if (type == "call")
            {
                var id = Text(block["id"]); var name = Text(block["name"]); var arguments = Text(block["arguments"]);
                if (protocol == LlmProtocol.ChatCompletions) calls.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } });
                else if (protocol == LlmProtocol.Responses) calls.Add(new JsonObject { ["type"] = "function_call", ["id"] = "fc_" + id, ["call_id"] = id, ["name"] = name, ["arguments"] = arguments,
                    ["status"] = doc.Finish is "length" or "content_filter" ? "incomplete" : "completed" });
                else blocks.Add(protocol == LlmProtocol.ClaudeMessages
                    ? new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = Object(JsonNode.Parse(arguments)).DeepClone() }
                    : new JsonObject { ["functionCall"] = new JsonObject { ["id"] = id, ["name"] = name, ["args"] = Object(JsonNode.Parse(arguments)).DeepClone() } });
            }
            else Loss(doc, "$response.content", "unsupported_content");
        }
        var usage = UsageMapping.Write(doc, protocol);
        JsonNode? usageValue = usage.Count == 0 ? null : usage;
        if (protocol == LlmProtocol.ChatCompletions)
        {
            if (message.Blocks.SkipWhile(b => Text(b["type"]) != "call").Any(b => Text(b["type"]) == "text")) Loss(doc, "$response.content", "block_order_changed");
            var chat = new JsonObject { ["role"] = "assistant", ["content"] = string.Concat(blocks.Select(b => Text(b!["text"]))) };
            if (calls.Count != 0) chat["tool_calls"] = calls;
            return new() { ["id"] = doc.Id, ["object"] = "chat.completion", ["created"] = doc.Created, ["model"] = doc.Model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = chat, ["finish_reason"] = doc.Finish }), ["usage"] = usageValue };
        }
        if (protocol == LlmProtocol.ClaudeMessages)
            return new() { ["id"] = doc.Id, ["type"] = "message", ["role"] = "assistant", ["model"] = doc.Model,
                ["content"] = blocks, ["stop_reason"] = doc.Finish switch { "length" => "max_tokens", "tool_calls" => "tool_use", "content_filter" => "refusal", _ => "end_turn" }, ["stop_sequence"] = null, ["usage"] = usage };
        if (protocol == LlmProtocol.Gemini)
            return new() { ["responseId"] = doc.Id, ["modelVersion"] = doc.Model, ["candidates"] = new JsonArray(new JsonObject { ["index"] = 0,
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = blocks }, ["finishReason"] = doc.Finish switch { "length" => "MAX_TOKENS", "content_filter" => "SAFETY", _ => "STOP" } }), ["usageMetadata"] = usage };
        var output = new JsonArray(); var textBlocks = new JsonArray(); var sequence = 0;
        void Flush()
        {
            if (textBlocks.Count == 0) return;
            output.Add(new JsonObject { ["type"] = "message", ["id"] = "msg_" + doc.Id + "_" + sequence++, ["status"] = doc.Finish is "length" or "content_filter" ? "incomplete" : "completed", ["role"] = "assistant", ["content"] = textBlocks }); textBlocks = [];
        }
        var callIndex = 0; var textIndex = 0;
        foreach (var block in message.Blocks)
        {
            if (Text(block["type"]) == "text") textBlocks.Add(blocks[textIndex++]!.DeepClone());
            else if (Text(block["type"]) == "call") { Flush(); output.Add(calls[callIndex++]!.DeepClone()); }
        }
        Flush();
        var response = new JsonObject { ["id"] = doc.Id, ["object"] = "response", ["created_at"] = doc.Created, ["model"] = doc.Model,
            ["status"] = doc.Finish is "length" or "content_filter" ? "incomplete" : "completed", ["output"] = output, ["usage"] = usageValue };
        if (doc.Finish is "length" or "content_filter") response["incomplete_details"] = new JsonObject { ["reason"] = doc.Finish == "length" ? "max_output_tokens" : "content_filter" };
        return response;
    }

    /// <summary>仅映射语义明确的终止原因，不猜测未知状态。</summary>
    internal static string Finish(string value, LlmProtocol protocol) => (protocol, value) switch
    {
        (LlmProtocol.ChatCompletions, "stop" or "length" or "tool_calls" or "content_filter") => value,
        (LlmProtocol.ClaudeMessages, "end_turn" or "stop_sequence") => "stop", (LlmProtocol.ClaudeMessages, "max_tokens") => "length",
        (LlmProtocol.ClaudeMessages, "tool_use") => "tool_calls", (LlmProtocol.ClaudeMessages, "refusal") => "content_filter",
        (LlmProtocol.Gemini, "STOP") => "stop", (LlmProtocol.Gemini, "MAX_TOKENS") => "length",
        (LlmProtocol.Gemini, "SAFETY") => "content_filter",
        _ => throw new LlmConversionException([new("unsupported_finish_reason", "$finish", "无法等价转换此终止原因。")])
    };

    /// <summary>明确的工具终态必须有工具调用，普通结束不能隐式改成工具执行。</summary>
    internal static void ValidateFinish(ConversionDocument doc)
    {
        var hasCalls = doc.Messages.SelectMany(m => m.Blocks).Any(b => Text(b["type"]) == "call");
        if (doc.Finish == "tool_calls" && !hasCalls || doc.Finish == "stop" && hasCalls)
            throw new InvalidDataException("工具调用与协议终止原因不一致。");
    }

}
