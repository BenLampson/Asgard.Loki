using System.Collections.ObjectModel;

namespace Asgard.Loki.LLM.Internal;

/// <summary>固定目标的显式格式策略；只检查原生格式意图，不转换 Schema、填充默认值或修改请求。</summary>
internal static class LlmOutputFormats
{
    /// <summary>只允许已有明确原生格式路径的单协议，复制并排序以稳定跨实例 Session 指纹。</summary>
    internal static LlmTarget Snapshot(LlmTarget target)
    {
        if (target.AllowedOutputFormats is not { } declarations) return target;
        Dictionary<LlmProtocol, IReadOnlyList<string>> copy = [];
        foreach (var (protocol, allowed) in declarations.OrderBy(pair => pair.Key))
        {
            if (!Supported(protocol) || !target.Protocols.HasFlag(protocol))
                throw new ArgumentException("输出格式声明必须对应目标已启用且具有格式校验的单个协议。", nameof(target));
            ArgumentNullException.ThrowIfNull(allowed);
            var values = allowed.ToArray();
            if (values.Any(value => value is not ("text" or "json_object" or "json_schema"))
                || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException("输出格式声明只接受不重复的 text、json_object、json_schema。", nameof(target));
            Array.Sort(values, StringComparer.Ordinal);
            copy.Add(protocol, Array.AsReadOnly(values));
        }
        return target with { AllowedOutputFormats = copy.Count == 0 ? null : new ReadOnlyDictionary<LlmProtocol, IReadOnlyList<string>>(copy) };
    }

    /// <summary>使用首次等待前冻结的原生请求；省略格式不推断默认值，显式 null/坏外壳不能绕过策略。</summary>
    internal static void Validate(LlmTarget target, LlmProtocol protocol, JsonObject? request)
    {
        if (request is null || target.AllowedOutputFormats is not { } declarations || !declarations.TryGetValue(protocol, out var allowed)) return;
        var (kind, path) = Read(request, protocol);
        if (kind is not null && !allowed.Contains(kind, StringComparer.Ordinal))
            throw new NotSupportedException($"目标 {target.Id} 的 {protocol} 不支持 {path} 请求的输出格式 {kind}；不允许自动降级或切换目标。");
        // Perplexity 原生 Responses 使用根 response_format；独立校验两处已知原生设置，不选优先级或移动字段。
        if (protocol == LlmProtocol.Responses)
        {
            var alternate = Typed(request, "response_format", "$");
            if (alternate.Kind is not null && !allowed.Contains(alternate.Kind, StringComparer.Ordinal))
                throw new NotSupportedException($"目标 {target.Id} 的 {protocol} 不支持 {alternate.Path} 请求的输出格式 {alternate.Kind}；不允许自动降级或切换目标。");
        }
    }

    /// <summary>格式语义仅覆盖下列已实现原生外壳，不因其他协议也有同名业务字段而猜测含义。</summary>
    private static bool Supported(LlmProtocol protocol) => protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses
        or LlmProtocol.ClaudeMessages or LlmProtocol.Gemini or LlmProtocol.OllamaChat or LlmProtocol.OllamaGenerate
        or LlmProtocol.BedrockConverse or LlmProtocol.CohereChat or LlmProtocol.WorkersAiText;

    /// <summary>读取各协议原生路径，不把工具参数 Schema 当作文本输出格式。</summary>
    private static (string? Kind, string Path) Read(JsonObject request, LlmProtocol protocol) => protocol switch
    {
        LlmProtocol.ChatCompletions or LlmProtocol.WorkersAiText => Typed(request, "response_format", "$"),
        LlmProtocol.Responses => Nested(request, "text", "format"),
        LlmProtocol.ClaudeMessages => Nested(request, "output_config", "format"),
        LlmProtocol.BedrockConverse => Nested(request, "outputConfig", "textFormat"),
        LlmProtocol.CohereChat => Cohere(request),
        LlmProtocol.OllamaChat or LlmProtocol.OllamaGenerate => Ollama(request),
        LlmProtocol.Gemini => Gemini(request),
        _ => throw new NotSupportedException("此协议未实现输出格式校验。")
    };

    /// <summary>父对象无格式字段时保持省略；明确 null 父对象同样不构造默认格式。</summary>
    private static (string? Kind, string Path) Nested(JsonObject request, string parent, string field)
    {
        if (!request.TryGetPropertyValue(parent, out var value) || value is null) return (null, "$" + "." + parent);
        return Typed(Object(value, "$." + parent), field, "$." + parent);
    }

    /// <summary>显式格式必须有非空字符串 type，其他原生字段与 schema 原文不动。</summary>
    private static (string? Kind, string Path) Typed(JsonObject parent, string field, string path)
    {
        path += "." + field;
        if (!parent.TryGetPropertyValue(field, out var value)) return (null, path);
        var format = Object(value, path);
        return (Text(format["type"], path + ".type"), path + ".type");
    }

    /// <summary>Cohere 的可选 schema 位于 json_object 外壳内，有 Schema 时独立检查 schema 能力。</summary>
    private static (string? Kind, string Path) Cohere(JsonObject request)
    {
        var result = Typed(request, "response_format", "$");
        if (result.Kind == "json_object" && request["response_format"]!.AsObject().ContainsKey("schema"))
            return ("json_schema", "$.response_format.schema");
        return result;
    }

    /// <summary>Ollama 原生字符串 json 表示 JSON 模式，对象才表示 Schema；空字符串表示普通文本。</summary>
    private static (string? Kind, string Path) Ollama(JsonObject request)
    {
        const string path = "$.format";
        if (!request.TryGetPropertyValue("format", out var format)) return (null, path);
        if (format is JsonObject) return ("json_schema", path);
        var text = Text(format, path, allowEmpty: true);
        return (text switch { "" => "text", "json" => "json_object", _ => "unsupported:" + text }, path);
    }

    /// <summary>识别 Gemini 新旧外壳；冲突字段不选优先级，媒体/枚举等格式不伪装成普通文本。</summary>
    private static (string? Kind, string Path) Gemini(JsonObject request)
    {
        const string path = "$.generationConfig";
        if (!request.TryGetPropertyValue("generationConfig", out var value) || value is null) return (null, path);
        var config = Object(value, path);
        var legacy = new[] { "responseMimeType", "responseSchema", "responseJsonSchema" }.Any(config.ContainsKey);
        if (config.TryGetPropertyValue("responseFormat", out var modern))
        {
            var format = Object(modern, path + ".responseFormat");
            if (format.TryGetPropertyValue("text", out var textNode))
            {
                if (legacy) throw new ArgumentException(path + " 新旧文本输出格式不能同时声明。");
                var text = Object(textNode, path + ".responseFormat.text");
                if (!text.ContainsKey("mimeType") && !text.ContainsKey("schema")) return (null, path + ".responseFormat.text");
                var mime = Text(text["mimeType"], path + ".responseFormat.text.mimeType");
                return (Mime(mime, text.ContainsKey("schema"), "TEXT_PLAIN", "APPLICATION_JSON"), path + ".responseFormat.text");
            }
        }
        if (!legacy) return (null, path);
        if (config.ContainsKey("responseSchema") && config.ContainsKey("responseJsonSchema"))
            throw new ArgumentException(path + " 不能同时声明两种 Schema。");
        var hasSchema = config.ContainsKey("responseSchema") || config.ContainsKey("responseJsonSchema");
        var legacyMime = Text(config["responseMimeType"], path + ".responseMimeType");
        return (Mime(legacyMime, hasSchema, "text/plain", "application/json"), path + ".responseMimeType");
    }

    /// <summary>格式分类不扩大 MIME 支持；只有明确 JSON MIME 下的 Schema 才具有 Schema 意图。</summary>
    private static string Mime(string mime, bool schema, string plain, string json)
    {
        if (schema && mime != json) throw new ArgumentException("Schema 输出必须显式选择原生 JSON MIME。");
        return mime == plain ? "text" : mime == json ? schema ? "json_schema" : "json_object" : "mime:" + mime;
    }

    /// <summary>不把 null、数组或标量解释为省略格式。</summary>
    private static JsonObject Object(JsonNode? value, string path)
        => value as JsonObject ?? throw new ArgumentException(path + " 必须为原生格式对象。");

    /// <summary>保留原始大小写，不将无效类型或空白静默转换成受支持格式。</summary>
    private static string Text(JsonNode? value, string path, bool allowEmpty = false)
        => value is JsonValue node && node.TryGetValue<string>(out var text) && (allowEmpty && text.Length == 0 || !string.IsNullOrWhiteSpace(text))
            ? text : throw new ArgumentException(path + " 必须为原生格式字符串。");
}
