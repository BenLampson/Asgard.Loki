using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>将描述性响应元数据和请求回显保留在原文中，不把它们解释为目标调用配置。</summary>
internal static class ResponseEnvelope
{
    /// <summary>仅移除明确分类的信封字段；业务输出、未知字段和不透明项仍由协议读取器验证。</summary>
    internal static JsonObject Content(JsonObject source, LlmProtocol protocol, ConversionDocument doc)
    {
        var content = source.DeepClone().AsObject();
        var fields = protocol switch
        {
            LlmProtocol.ChatCompletions => new[] { "system_fingerprint", "service_tier" },
            LlmProtocol.Responses => ["service_tier", "metadata", "completed_at", "instructions", "temperature", "top_p",
                "max_output_tokens", "max_tool_calls", "parallel_tool_calls", "previous_response_id", "store", "background",
                "user", "safety_identifier", "prompt_cache_key", "prompt_cache_retention", "truncation", "tools", "tool_choice", "reasoning", "text"],
            LlmProtocol.Gemini => ["createTime"],
            _ => []
        };
        foreach (var field in fields)
        {
            if (!content.TryGetPropertyValue(field, out var value)) continue;
            if (value is not null) Validate(field, value, doc);
            content.Remove(field);
        }
        return content;
    }

    /// <summary>请求回显只说明上游实际执行上下文，不授权重发、响应链或工具执行。</summary>
    private static void Validate(string field, JsonNode value, ConversionDocument doc)
    {
        switch (field)
        {
            case "metadata":
                foreach (var entry in Object(value)) _ = Text(entry.Value);
                break;
            case "reasoning":
                var reasoning = Object(value); Check(reasoning, doc, "$envelope.reasoning", "effort", "summary", "generate_summary");
                foreach (var entry in reasoning) if (entry.Value is not null) _ = Text(entry.Value);
                break;
            case "text":
                var text = Object(value); Check(text, doc, "$envelope.text", "format", "verbosity");
                if (text["format"] is not null)
                {
                    var format = Object(text["format"]); Check(format, doc, "$envelope.text.format", "type", "name", "schema", "strict", "description");
                    if (format["schema"] is not null) _ = Object(format["schema"]);
                }
                if (text["verbosity"] is not null) _ = Text(text["verbosity"]);
                break;
            case "tools": _ = Array(value); break;
            case "tool_choice": if (value is not JsonObject) _ = Text(value); break;
            case "store": case "background": case "parallel_tool_calls": _ = value.GetValue<bool>(); break;
            case "completed_at": case "max_output_tokens": case "max_tool_calls": _ = Number(value); break;
            case "temperature": case "top_p": _ = System.Text.Json.JsonSerializer.SerializeToElement(value).GetDouble(); break;
            default: _ = Text(value); break;
        }
    }
}
