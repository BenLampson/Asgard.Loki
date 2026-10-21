using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>区分现行 responseFormat 文本外壳与旧格式字段；不改变 JSON Schema 约束。</summary>
internal static class GeminiOutputMapping
{
    /// <summary>读取明确指定的文本格式；未指定文本时不创造默认输出意图。</summary>
    internal static JsonObject? Read(JsonObject config, ConversionDocument doc, LlmProtocol target)
    {
        if (config.ContainsKey("responseFormat"))
        {
            var modern = Object(config["responseFormat"]); Check(modern, doc, "$.generationConfig.responseFormat", "text");
            if (modern.ContainsKey("text"))
            {
                if (new[] { "responseSchema", "responseJsonSchema", "responseMimeType" }.Any(config.ContainsKey))
                    throw new ArgumentException("Gemini 文本输出不能混合现行与旧格式外壳。");
                return ReadText(Object(modern["text"]), doc);
            }
        }
        if (config.ContainsKey("responseSchema") && config.ContainsKey("responseJsonSchema"))
            throw new ArgumentException("Gemini 不能同时指定两种 Schema 方言。");
        if (config.ContainsKey("responseSchema") && config["responseMimeType"]?.GetValue<string>() != "application/json")
            throw new ArgumentException("Gemini responseSchema 必须使用 application/json。");
        if (config.ContainsKey("responseJsonSchema") && config["responseMimeType"]?.GetValue<string>() != "application/json")
            throw new ArgumentException("Gemini responseJsonSchema 必须使用 application/json。");
        if (!config.ContainsKey("responseMimeType")) return null;
        var mime = Text(config["responseMimeType"]);
        if (mime is not ("application/json" or "text/plain")) { Loss(doc, "$.generationConfig.responseMimeType"); return null; }
        var format = new JsonObject { ["type"] = mime == "text/plain" ? "text" : "json_object" };
        if (config.ContainsKey("responseJsonSchema"))
        {
            format["type"] = "json_schema"; format["schema"] = Object(config["responseJsonSchema"]).DeepClone(); format["strict"] = true;
        }
        if (config.ContainsKey("responseSchema"))
        {
            format["type"] = "json_schema"; format["strict"] = true;
            format["schema"] = GeminiSchemaMapping.Read(Object(config["responseSchema"]), doc, "$.generationConfig.responseSchema", target);
        }
        return format;
    }

    /// <summary>只把 APPLICATION_JSON/TEXT_PLAIN 映射到已知文本意图；其他模态仍诊断。</summary>
    private static JsonObject? ReadText(JsonObject text, ConversionDocument doc)
    {
        const string path = "$.generationConfig.responseFormat.text";
        Check(text, doc, path, "mimeType", "schema");
        if (text.ContainsKey("schema") && text["mimeType"]?.GetValue<string>() != "APPLICATION_JSON")
            throw new ArgumentException("Gemini responseFormat.text.schema 必须使用 APPLICATION_JSON。");
        if (!text.ContainsKey("mimeType")) return null;
        var mime = Text(text["mimeType"]);
        if (mime is not ("APPLICATION_JSON" or "TEXT_PLAIN")) { Loss(doc, path + ".mimeType", "unsupported_output_mime"); return null; }
        var format = new JsonObject { ["type"] = mime == "TEXT_PLAIN" ? "text" : "json_object" };
        if (text.ContainsKey("schema"))
        {
            format["type"] = "json_schema"; format["schema"] = Object(text["schema"]).DeepClone(); format["strict"] = true;
        }
        return format;
    }

    /// <summary>目标格式由调用者明确选择；默认保留旧 JSON Schema 外壳，不自动迁移模型配置。</summary>
    internal static void Write(JsonObject config, JsonObject format, LlmConversionOptions options, ConversionDocument doc)
    {
        var type = Text(format["type"]);
        if (options.GeminiOutputFormat == GeminiOutputFormat.ResponseFormat)
        {
            var text = new JsonObject { ["mimeType"] = type == "text" ? "TEXT_PLAIN" : "APPLICATION_JSON" };
            if (type == "json_schema") text["schema"] = format["schema"]!.DeepClone();
            config["responseFormat"] = new JsonObject { ["text"] = text };
        }
        else
        {
            config["responseMimeType"] = type == "text" ? "text/plain" : "application/json";
            if (type == "json_schema")
            {
                var typed = options.GeminiOutputFormat == GeminiOutputFormat.LegacySchema;
                config[typed ? "responseSchema" : "responseJsonSchema"] = typed
                    ? GeminiSchemaWriter.Write(Object(format["schema"]), doc, "$format.schema", doc.OutputSchemaOrdered) : format["schema"]!.DeepClone();
            }
        }
    }
}
