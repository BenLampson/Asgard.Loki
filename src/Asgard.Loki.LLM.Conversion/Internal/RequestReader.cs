using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>读取四种原生请求；所有未识别字段形成损耗诊断。</summary>
internal static class RequestReader
{
    /// <summary>转换为单次调用内的有序内容，输入对象不被修改。</summary>
    internal static ConversionDocument Read(JsonObject source, LlmProtocol protocol, LlmProtocol target = LlmProtocol.None)
    {
        var doc = new ConversionDocument { Model = Text(source["model"]) };
        switch (protocol)
        {
            case LlmProtocol.ChatCompletions: Chat(source, doc); break;
            case LlmProtocol.Responses: Responses(source, doc); break;
            case LlmProtocol.ClaudeMessages: Claude(source, doc); break;
            case LlmProtocol.Gemini: Gemini(source, doc); break;
        }
        ToolMapping.Read(source, protocol, doc);
        ReasoningMapping.Read(source, protocol, doc);
        StructuredOutputMapping.Read(source, protocol, doc, target);
        return doc;
    }

    /// <summary>读取 Chat 消息与公共生成参数。</summary>
    private static void Chat(JsonObject source, ConversionDocument doc)
    {
        Check(source, doc, "$", "model", "messages", "stream", "temperature", "top_p", "max_tokens", "max_completion_tokens", "stop", "tools", "tool_choice", "response_format", "reasoning_effort", "parallel_tool_calls");
        if (source.ContainsKey("max_tokens") && source.ContainsKey("max_completion_tokens"))
            throw new ArgumentException("不能同时转换两个输出上限字段。");
        Settings(source, doc, ("temperature", "temperature"), ("top_p", "topP"), ("max_tokens", "maxTokens"),
            ("max_completion_tokens", "maxTokens"), ("stop", "stop"), ("parallel_tool_calls", "parallel"));
        foreach (var node in Array(source["messages"]))
        {
            var item = Object(node);
            var message = new ConversionMessage(Text(item["role"]));
            Check(item, doc, "$.messages[]", message.Role == "tool" ? ["role", "content", "tool_call_id"]
                : message.Role == "assistant" ? ["role", "content", "tool_calls"] : ["role", "content"]);
            if (message.Role == "tool")
                message.Blocks.Add(ToolResultMapping.Read(Text(item["tool_call_id"]), item["content"], LlmProtocol.ChatCompletions, doc));
            else
            {
                ContentMapping.Read(item["content"], LlmProtocol.ChatCompletions, message, doc);
                if (item["tool_calls"] is JsonArray calls)
                    foreach (var call in calls)
                    {
                        var c = Object(call);
                        Check(c, doc, "$.messages[].tool_calls[]", "id", "type", "function");
                        if (Text(c["type"]) != "function") { Loss(doc, "$.messages[].tool_calls[].type"); continue; }
                        var f = Object(c["function"]);
                        Check(f, doc, "$.messages[].tool_calls[].function", "name", "arguments");
                        message.Blocks.Add(new() { ["type"] = "call", ["id"] = Text(c["id"]), ["name"] = Text(f["name"]), ["arguments"] = Text(f["arguments"]) });
                    }
            }
            doc.Messages.Add(message);
        }
    }

    /// <summary>保留 Responses 原生项的先后顺序，不把历史标识转成文本。</summary>
    private static void Responses(JsonObject source, ConversionDocument doc)
    {
        Check(source, doc, "$", "model", "input", "instructions", "stream", "temperature", "top_p", "max_output_tokens", "tools", "tool_choice", "text", "reasoning", "parallel_tool_calls");
        Settings(source, doc, ("temperature", "temperature"), ("top_p", "topP"), ("max_output_tokens", "maxTokens"), ("parallel_tool_calls", "parallel"));
        if (source["instructions"] is not null)
        {
            var system = new ConversionMessage("system");
            system.Blocks.Add(new() { ["type"] = "text", ["text"] = Text(source["instructions"]) });
            doc.Messages.Add(system);
        }
        if (source["input"] is JsonValue value)
        {
            var message = new ConversionMessage("user");
            message.Blocks.Add(new() { ["type"] = "text", ["text"] = value.GetValue<string>() });
            doc.Messages.Add(message);
            return;
        }
        foreach (var node in Array(source["input"]))
        {
            var item = Object(node);
            var type = item["type"]?.GetValue<string>() ?? "message";
            if (type == "message")
            {
                Check(item, doc, "$.input[]", "type", "role", "content");
                var message = new ConversionMessage(Text(item["role"]));
                ContentMapping.Read(item["content"], LlmProtocol.Responses, message, doc);
                doc.Messages.Add(message);
            }
            else if (type == "function_call")
            {
                Check(item, doc, "$.input[]", "type", "call_id", "name", "arguments");
                var message = new ConversionMessage("assistant");
                message.Blocks.Add(new() { ["type"] = "call", ["id"] = Text(item["call_id"]), ["name"] = Text(item["name"]), ["arguments"] = Text(item["arguments"]) });
                doc.Messages.Add(message);
            }
            else if (type == "function_call_output")
            {
                Check(item, doc, "$.input[]", "type", "call_id", "output");
                var message = new ConversionMessage("tool");
                message.Blocks.Add(ToolResultMapping.Read(Text(item["call_id"]), item["output"], LlmProtocol.Responses, doc));
                doc.Messages.Add(message);
            }
            else if (type is "reasoning" or "compaction") throw new LlmConversionException([new("opaque_state", "$.input[]." + type, "不透明推理或压缩项必须通过原生协议回传。")]);
            else Loss(doc, "$.input[]." + type, "unsupported_item");
        }
    }

    /// <summary>读取 Claude 系统块、消息及工具结果。</summary>
    private static void Claude(JsonObject source, ConversionDocument doc)
    {
        Check(source, doc, "$", "model", "messages", "system", "max_tokens", "stream", "temperature", "top_p", "top_k", "stop_sequences", "tools", "tool_choice", "thinking", "output_config");
        Settings(source, doc, ("temperature", "temperature"), ("top_p", "topP"), ("top_k", "topK"), ("max_tokens", "maxTokens"), ("stop_sequences", "stop"));
        if (source["system"] is not null)
        {
            var system = new ConversionMessage("system");
            ContentMapping.Read(source["system"], LlmProtocol.ClaudeMessages, system, doc);
            doc.Messages.Add(system);
        }
        foreach (var node in Array(source["messages"]))
        {
            var item = Object(node);
            Check(item, doc, "$.messages[]", "role", "content");
            var message = new ConversionMessage(Text(item["role"]));
            ContentMapping.Read(item["content"], LlmProtocol.ClaudeMessages, message, doc);
            doc.Messages.Add(message);
        }
    }

    /// <summary>读取 Gemini 原生内容与生成配置。</summary>
    private static void Gemini(JsonObject source, ConversionDocument doc)
    {
        Check(source, doc, "$", "model", "stream", "contents", "systemInstruction", "generationConfig", "tools", "toolConfig");
        if (source.ContainsKey("generationConfig"))
        {
            var config = Object(source["generationConfig"]);
            Check(config, doc, "$.generationConfig", "temperature", "topP", "topK", "maxOutputTokens", "stopSequences", "thinkingConfig", "responseMimeType", "responseJsonSchema", "responseSchema", "responseFormat");
            Settings(config, doc, ("temperature", "temperature"), ("topP", "topP"), ("topK", "topK"), ("maxOutputTokens", "maxTokens"), ("stopSequences", "stop"));
        }
        if (source.ContainsKey("systemInstruction"))
        {
            var system = Object(source["systemInstruction"]);
            Check(system, doc, "$.systemInstruction", "parts");
            var message = new ConversionMessage("system");
            ContentMapping.Read(system["parts"], LlmProtocol.Gemini, message, doc);
            doc.Messages.Add(message);
        }
        var contents = Array(source["contents"]);
        foreach (var node in contents)
            ContentMapping.ReserveGeminiIds(Array(Object(node)["parts"]), doc);
        foreach (var node in contents)
        {
            var item = Object(node);
            Check(item, doc, "$.contents[]", "role", "parts");
            var role = Text(item["role"]);
            var message = new ConversionMessage(role == "model" ? "assistant" : role);
            ContentMapping.Read(item["parts"], LlmProtocol.Gemini, message, doc);
            doc.Messages.Add(message);
        }
    }

    /// <summary>保留零、false、null 与省略之间的区别。</summary>
    private static void Settings(JsonObject source, ConversionDocument doc, params (string From, string To)[] names)
    {
        foreach (var (from, to) in names) Copy(source, from, doc.Settings, to);
    }
}
