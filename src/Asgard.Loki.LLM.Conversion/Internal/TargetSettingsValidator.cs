using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>仅校验转换完成后的实际目标字段；声明不能覆盖协议限制或授权丢弃字段。</summary>
internal static class TargetSettingsValidator
{
    /// <summary>配置必须明确有效；不使用模型名、网络探测或未知值回退。</summary>
    internal static void ValidateConfiguration(LlmConversionTargetSettings? settings)
    {
        if (settings is null) return;
        ValidateValues(settings.AllowedReasoningEfforts, ["none", "minimal", "low", "medium", "high", "xhigh", "max"]);
        ValidateValues(settings.AllowedThinkingModes, ["manual", "adaptive", "disabled"]);
        ValidateValues(settings.AllowedOutputFormats, ["text", "json_object", "json_schema"]);
        if (settings.MaximumOutputTokens is <= 0 || settings.MinimumThinkingBudgetTokens is <= 0 || settings.MaximumThinkingBudgetTokens is <= 0
            || settings.MinimumThinkingBudgetTokens > settings.MaximumThinkingBudgetTokens)
            throw new ArgumentOutOfRangeException(nameof(settings), "目标声明的 token 范围无效。");
    }

    /// <summary>按已生成的原生请求校验，默认未声明的维度不增加限制；任何违反均硬失败。</summary>
    internal static void Validate(JsonObject target, LlmProtocol protocol, LlmConversionTargetSettings? settings)
    {
        if (settings is null) return;
        List<LlmConversionDiagnostic> diagnostics = [];
        var config = protocol == LlmProtocol.Gemini ? target["generationConfig"] as JsonObject : target;
        if (settings.SupportsSampling == false && config is not null)
            foreach (var key in protocol == LlmProtocol.Gemini ? new[] { "temperature", "topP", "topK" } : ["temperature", "top_p", "top_k"])
                Forbid(config, key, protocol == LlmProtocol.Gemini ? "$.generationConfig" : "$", diagnostics);
        var outputKey = protocol switch
        {
            LlmProtocol.ChatCompletions => "max_completion_tokens", LlmProtocol.Responses => "max_output_tokens",
            LlmProtocol.ClaudeMessages => "max_tokens", _ => "maxOutputTokens"
        };
        if (settings.MaximumOutputTokens is { } outputMaximum && config is not null && config.ContainsKey(outputKey))
        {
            var path = (protocol == LlmProtocol.Gemini ? "$.generationConfig." : "$.") + outputKey;
            var limit = Integer(config[outputKey], path);
            if (limit <= 0) throw new ArgumentException($"{path} 必须是正整数。");
            if (limit > outputMaximum) Add(diagnostics, path, "target_output_limit_exceeded");
        }
        var reasoning = target["reasoning"] as JsonObject;
        var output = target["output_config"] as JsonObject;
        var thinking = protocol == LlmProtocol.Gemini ? config?["thinkingConfig"] as JsonObject : target["thinking"] as JsonObject;
        if (protocol == LlmProtocol.ChatCompletions) Allowed(target, "reasoning_effort", settings.AllowedReasoningEfforts, "$", diagnostics);
        if (protocol == LlmProtocol.Responses) Allowed(reasoning, "effort", settings.AllowedReasoningEfforts, "$.reasoning", diagnostics);
        if (protocol == LlmProtocol.ClaudeMessages) Allowed(output, "effort", settings.AllowedReasoningEfforts, "$.output_config", diagnostics);
        if (protocol == LlmProtocol.Gemini) Allowed(thinking, "thinkingLevel", settings.AllowedReasoningEfforts, "$.generationConfig.thinkingConfig", diagnostics, lower: true);
        if (thinking is not null && protocol is LlmProtocol.ClaudeMessages or LlmProtocol.Gemini)
            Thinking(thinking, protocol, settings, diagnostics);
        if (settings.SupportsReasoningOutputControl == false)
        {
            if (protocol == LlmProtocol.Responses) Forbid(reasoning, "summary", "$.reasoning", diagnostics);
            if (protocol == LlmProtocol.ClaudeMessages) Forbid(thinking, "display", "$.thinking", diagnostics);
            if (protocol == LlmProtocol.Gemini) Forbid(thinking, "includeThoughts", "$.generationConfig.thinkingConfig", diagnostics);
        }
        if (settings.SupportsParallelToolControl == false)
        {
            if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses) Forbid(target, "parallel_tool_calls", "$", diagnostics);
            if (protocol == LlmProtocol.ClaudeMessages) Forbid(target["tool_choice"] as JsonObject, "disable_parallel_tool_use", "$.tool_choice", diagnostics);
        }
        OutputFormat(target, protocol, settings.AllowedOutputFormats, diagnostics);
        if (diagnostics.Count != 0) throw new LlmConversionException(diagnostics);
    }

    /// <summary>仅手动正数预算应用上下界；关闭和动态控制保持原生含义。</summary>
    private static void Thinking(JsonObject thinking, LlmProtocol protocol, LlmConversionTargetSettings settings, List<LlmConversionDiagnostic> diagnostics)
    {
        var gemini = protocol == LlmProtocol.Gemini;
        var key = gemini ? "thinkingBudget" : "budget_tokens";
        var path = gemini ? "$.generationConfig.thinkingConfig" : "$.thinking";
        string? mode = null; long? budget = null;
        if (gemini && thinking.ContainsKey(key))
        {
            var native = Integer(thinking[key], path + "." + key);
            mode = native == 0 ? "disabled" : native == -1 ? "adaptive" : "manual";
            if (native > 0) budget = native;
        }
        if (!gemini && thinking.ContainsKey("type"))
        {
            mode = Text(thinking["type"]) switch { "enabled" => "manual", "adaptive" => "adaptive", "disabled" => "disabled", _ => null };
            if (mode == "manual") budget = Integer(thinking[key], path + "." + key);
        }
        if (mode is not null && settings.AllowedThinkingModes is { } allowed && !allowed.Contains(mode, StringComparer.Ordinal))
            Add(diagnostics, path + (gemini ? ".thinkingBudget" : ".type"), "target_thinking_mode_not_supported");
        if (budget is { } manual && (manual < settings.MinimumThinkingBudgetTokens || manual > settings.MaximumThinkingBudgetTokens))
            Add(diagnostics, path + "." + key, "target_thinking_budget_out_of_range");
    }

    /// <summary>检查实际写出的格式意图；未设置格式时不假定上游默认输出。</summary>
    private static void OutputFormat(JsonObject target, LlmProtocol protocol, IReadOnlyList<string>? allowed, List<LlmConversionDiagnostic> diagnostics)
    {
        if (allowed is null) return;
        JsonObject? format = protocol switch
        {
            LlmProtocol.ChatCompletions => target["response_format"] as JsonObject,
            LlmProtocol.Responses => target["text"]?["format"] as JsonObject,
            LlmProtocol.ClaudeMessages => target["output_config"]?["format"] as JsonObject, _ => null
        };
        var path = protocol switch
        {
            LlmProtocol.ChatCompletions => "$.response_format.type", LlmProtocol.Responses => "$.text.format.type",
            LlmProtocol.ClaudeMessages => "$.output_config.format.type", _ => "$.generationConfig.responseMimeType"
        };
        string? kind = format?["type"]?.GetValue<string>();
        if (protocol == LlmProtocol.Gemini && target["generationConfig"] is JsonObject config)
        {
            if (config["responseFormat"]?["text"] is JsonObject text)
            {
                path = "$.generationConfig.responseFormat.text.mimeType";
                kind = text["mimeType"]?.GetValue<string>() == "TEXT_PLAIN" ? "text" : text.ContainsKey("schema") ? "json_schema" : "json_object";
            }
            else if (config.ContainsKey("responseMimeType"))
                kind = config["responseMimeType"]?.GetValue<string>() == "text/plain" ? "text"
                    : config.ContainsKey("responseSchema") || config.ContainsKey("responseJsonSchema") ? "json_schema" : "json_object";
        }
        if (kind is not null && !allowed.Contains(kind, StringComparer.Ordinal)) Add(diagnostics, path, "target_output_format_not_supported");
    }

    /// <summary>明确空列表拒绝字段的任何显式值；null 字段不能被当成省略。</summary>
    private static void Allowed(JsonObject? node, string key, IReadOnlyList<string>? allowed, string path, List<LlmConversionDiagnostic> diagnostics, bool lower = false)
    {
        if (allowed is null || node is null || !node.ContainsKey(key)) return;
        var value = node[key]?.GetValue<string>();
        if (lower) value = value?.ToLowerInvariant();
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal)) Add(diagnostics, path + "." + key, "target_reasoning_effort_not_supported");
    }
    /// <summary>不支持字段时保留零、false、null 与省略的差别，不能删除参数后继续发送。</summary>
    private static void Forbid(JsonObject? node, string key, string path, List<LlmConversionDiagnostic> diagnostics)
    {
        if (node?.ContainsKey(key) == true) Add(diagnostics, path + "." + key, "target_setting_not_supported");
    }
    /// <summary>校验列表值而不回显调用者误填的敏感字符串。</summary>
    private static void ValidateValues(IReadOnlyList<string>? values, string[] known)
    {
        if (values?.Any(value => !known.Contains(value, StringComparer.Ordinal)) == true) throw new ArgumentException("目标设置支持声明包含未知值。");
    }
    /// <summary>协议计数按 JSON 整数读取，显式 null 不得成为零。</summary>
    private static long Integer(JsonNode? value, string path)
        => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number && JsonSerializer.SerializeToElement(value).TryGetInt64(out var result)
            ? result : throw new ArgumentException($"{path} 必须是 JSON 整数。");
    /// <summary>声明违反是硬限制，不能被 AllowLoss 掩盖。</summary>
    private static void Add(List<LlmConversionDiagnostic> diagnostics, string path, string code)
        => diagnostics.Add(new(code, path, "转换后的目标字段不符合调用者声明的设置支持范围。"));
}
