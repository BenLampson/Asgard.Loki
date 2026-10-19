using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式映射结构化输出外壳；JSON Schema 原文不改写、不删约束。</summary>
internal static class StructuredOutputMapping
{
    /// <summary>按原生字段读取格式；区分 JSON Schema 与 Gemini 的另一种 Schema 方言。</summary>
    internal static void Read(JsonObject source, LlmProtocol protocol, ConversionDocument doc)
    {
        JsonObject? format = null;
        if (protocol == LlmProtocol.ChatCompletions && source.ContainsKey("response_format"))
        {
            var wrapper = Object(source["response_format"]); var type = Text(wrapper["type"]);
            Check(wrapper, doc, "$.response_format", type == "json_schema" ? ["type", "json_schema"] : ["type"]);
            format = type == "json_schema" ? Object(wrapper["json_schema"]).DeepClone().AsObject() : new();
            format["type"] = type;
        }
        else if (protocol == LlmProtocol.Responses && source.ContainsKey("text"))
        {
            var text = Object(source["text"]); Check(text, doc, "$.text", "format");
            if (text.ContainsKey("format")) format = Object(text["format"]).DeepClone().AsObject();
        }
        else if (protocol == LlmProtocol.ClaudeMessages && source.ContainsKey("output_config"))
        {
            var output = Object(source["output_config"]); Check(output, doc, "$.output_config", "format", "effort");
            if (output.ContainsKey("format"))
            {
                var native = Object(output["format"]); Check(native, doc, "$.output_config.format", "type", "schema");
                if (Text(native["type"]) != "json_schema") throw new ArgumentException("Claude output_config.format 仅支持 json_schema。");
                format = native.DeepClone().AsObject(); format["strict"] = true;
            }
        }
        else if (protocol == LlmProtocol.Gemini && source["generationConfig"] is JsonObject config)
            format = GeminiOutputMapping.Read(config, doc);
        if (format is null) return;
        var kind = Text(format["type"]);
        Check(format, doc, "$format", kind == "json_schema" ? ["type", "name", "description", "schema", "strict"] : ["type"]);
        if (kind is not ("text" or "json_object" or "json_schema")) { Loss(doc, "$format.type"); return; }
        if (kind == "json_schema")
        {
            _ = Object(format["schema"]);
            if (format["strict"] is not null) _ = format["strict"]!.GetValue<bool>();
            if (format.ContainsKey("name")) ValidateName(Text(format["name"]));
            if (format.ContainsKey("description")) _ = Text(format["description"]);
        }
        doc.OutputFormat = format;
    }

    /// <summary>只更改原生格式外壳；不为不支持的目标伪造通用 JSON 工具。</summary>
    internal static void Write(JsonObject target, LlmProtocol protocol, ConversionDocument doc, LlmConversionOptions options)
    {
        if (doc.OutputFormat is not { } original) return;
        var format = original.DeepClone().AsObject(); var type = Text(format["type"]);
        if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses)
        {
            if (type == "json_schema" && !format.ContainsKey("name"))
                format["name"] = options.DefaultSchemaName ?? throw new ArgumentException("转换到 OpenAI JSON Schema 必须显式提供 DefaultSchemaName。");
            if (protocol == LlmProtocol.Responses) target["text"] = new JsonObject { ["format"] = format };
            else
            {
                format.Remove("type");
                target["response_format"] = type == "json_schema" ? new JsonObject { ["type"] = type, ["json_schema"] = format }
                    : new JsonObject { ["type"] = type };
            }
            return;
        }
        if (type == "json_schema")
        {
            foreach (var key in new[] { "name", "description" })
                if (format.ContainsKey(key)) Loss(doc, "$format." + key, "format_metadata_not_representable");
            if (format["strict"]?.GetValue<bool>() != true) Loss(doc, "$format.strict", "schema_enforcement_changed");
        }
        if (protocol == LlmProtocol.Gemini)
            GeminiOutputMapping.Write(Ensure(target, "generationConfig"), format, options);
        else if (type == "json_schema") Ensure(target, "output_config")["format"] = new JsonObject { ["type"] = type, ["schema"] = format["schema"]!.DeepClone() };
        else if (type != "text") Loss(doc, "$format", "unsupported_output_format");
    }

    /// <summary>复用同一原生配置对象，避免格式覆盖推理配置。</summary>
    internal static JsonObject Ensure(JsonObject target, string key)
    {
        if (target[key] is JsonObject found) return found;
        var result = new JsonObject(); target[key] = result; return result;
    }
    /// <summary>名称用于目标 Schema 标识，不能静默清洗无效字符。</summary>
    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("无效的 JSON Schema 名称。");
    }
}
