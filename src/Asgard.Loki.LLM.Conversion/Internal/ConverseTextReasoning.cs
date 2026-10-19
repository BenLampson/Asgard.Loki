using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式模型方言的请求设置转换，不估算预算、改输出上限或移植不透明推理内容。</summary>
internal static class ConverseTextReasoning
{
    /// <summary>Anthropic 与 Nova2 设置分开写出，通用生成控制和输出 Schema 保持独立。</summary>
    internal static void Write(JsonObject target, ConversionDocument doc, LlmProtocol source, LlmConversionOptions options)
    {
        if (doc.Reasoning.Count == 0 && !doc.Settings.ContainsKey("topK")) return;
        if (options.BedrockConverseModelDialect == BedrockConverseModelDialect.Unspecified)
            BedrockConversion.Reject("$reasoning", "converse_model_dialect_required");
        JsonObject fields = [];
        if (doc.Settings.ContainsKey("topK"))
        {
            var value = JsonSerializer.SerializeToElement(doc.Settings["topK"]);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0
                || options.BedrockConverseModelDialect == BedrockConverseModelDialect.AmazonNova2 && count > 128)
                throw new ArgumentException("topK 必须是所选方言允许的非负原生整数。");
            fields[options.BedrockConverseModelDialect == BedrockConverseModelDialect.Anthropic ? "top_k" : "topK"] = count;
        }
        if (options.BedrockConverseModelDialect == BedrockConverseModelDialect.Anthropic)
        {
            if (options.BedrockConverseInferenceFormat != BedrockConverseInferenceFormat.Unspecified || options.TargetReasoningEffort is not null)
                throw new ArgumentException("Anthropic 方言不能使用 Nova 包装或预算到 effort 的替代策略。");
            Anthropic(fields, target, doc, source, options);
            if (fields.Count != 0) target["additionalModelRequestFields"] = fields;
        }
        else
        {
            if (options.BedrockConverseInferenceFormat == BedrockConverseInferenceFormat.Unspecified)
                throw new ArgumentException("Nova2 方言必须明确选择 Converse 参数包装位置。");
            if (options.TargetThinkingBudgetTokens is not null) throw new ArgumentException("Nova2 不接受 Claude 手动预算策略。");
            Nova(fields, target, doc, options);
            if (fields.Count != 0) target["additionalModelRequestFields"] = options.BedrockConverseInferenceFormat == BedrockConverseInferenceFormat.Direct
                ? fields : new JsonObject { ["inferenceConfig"] = fields };
        }
    }

    /// <summary>明确保持手动预算、关闭和自适应意图，数字预算必须小于原始显式输出上限。</summary>
    private static void Anthropic(JsonObject fields, JsonObject target, ConversionDocument doc, LlmProtocol source, LlmConversionOptions options)
    {
        var r = doc.Reasoning; if (r.Count == 0) return;
        var effort = Effort(r); var mode = r["mode"]?.GetValue<string>(); long? budget = r["budget"] is null ? null : Number(r["budget"]);
        if (effort == "minimal") BedrockConversion.Reject("$reasoning.effort", "unsupported_reasoning_effort");
        if (effort == "none")
        {
            if (mode is "enabled" or "adaptive" || budget is not null) throw new ArgumentException("显式关闭推理与启用模式冲突。");
            mode = "disabled";
        }
        if (options.TargetThinkingBudgetTokens is { } configured)
        {
            if (mode == "disabled" || budget is not null && budget != configured) throw new ArgumentException("目标预算与源关闭/预算冲突。");
            if (mode == "adaptive") Loss(doc, "$reasoning.mode", "reasoning_mode_changed");
            budget = configured; mode = "enabled";
        }
        if (budget is not null)
        {
            var limit = target["inferenceConfig"]?["maxTokens"];
            if (mode == "disabled" || budget < 1024 || limit is null || budget >= Number(limit))
                throw new ArgumentException("Anthropic 预算至少 1024，且必须小于原始显式输出上限。");
            mode = "enabled";
        }
        if (mode is null && effort is not null && source != LlmProtocol.ClaudeMessages) mode = "adaptive";
        if (mode == "disabled" && effort is "xhigh" or "max") throw new ArgumentException("关闭推理不能使用 xhigh/max effort。");
        if (mode is "enabled" or "adaptive")
        {
            if (new[] { "temperature", "topP", "topK" }.Any(doc.Settings.ContainsKey)
                || doc.ToolChoice is JsonObject || doc.ToolChoice is JsonValue choice && Text(choice) == "required")
                throw new ArgumentException("Anthropic 推理不能通过删除采样或强制工具选择来规避冲突。");
            if (doc.Messages.LastOrDefault(message => message.Role is not ("system" or "developer"))?.Role == "assistant")
                throw new ArgumentException("Anthropic 推理不允许 assistant 预填充。");
        }
        if (mode is not null)
        {
            var thinking = new JsonObject { ["type"] = mode }; if (budget is not null) thinking["budget_tokens"] = budget.Value;
            if (r.ContainsKey("include"))
            {
                var include = Boolean(r["include"]);
                if (mode == "disabled" && include) throw new ArgumentException("关闭推理与显示思考输出冲突。");
                if (mode != "disabled") thinking["display"] = include ? "summarized" : "omitted";
            }
            fields["thinking"] = thinking;
        }
        else if (r.ContainsKey("include")) BedrockConversion.Reject("$reasoning.include", "thinking_mode_required");
        if (r["summary"] is not null && Text(r["summary"]) != "auto") Loss(doc, "$reasoning.summary", "reasoning_summary_detail_changed");
        if (effort is not (null or "none")) fields["output_config"] = new JsonObject { ["effort"] = effort };
    }

    /// <summary>Nova2 只接受定性强度；数字预算必须显式替代并留下诊断，高强度不能携带输出上限或采样。</summary>
    private static void Nova(JsonObject fields, JsonObject target, ConversionDocument doc, LlmConversionOptions options)
    {
        var r = doc.Reasoning; if (r.Count == 0) return;
        var effort = Effort(r); var mode = r["mode"]?.GetValue<string>();
        if (effort == "none")
        {
            if (mode is "enabled" or "adaptive" || r.ContainsKey("budget")) throw new ArgumentException("关闭推理与源启用意图冲突。");
            mode = "disabled";
        }
        if (r.ContainsKey("budget"))
        {
            if (options.TargetReasoningEffort is not ("low" or "medium" or "high"))
                BedrockConversion.Reject("$reasoning.budget", "converse_reasoning_budget_requires_explicit_replacement");
            if (effort is not null && effort != options.TargetReasoningEffort) Loss(doc, "$reasoning.effort", "reasoning_effort_replaced");
            effort = options.TargetReasoningEffort; Loss(doc, "$reasoning.budget", "reasoning_budget_replaced");
        }
        else if (options.TargetReasoningEffort is not null) throw new ArgumentException("目标 effort 替代策略需要源数字预算，不能静默覆盖定性强度。");
        if (mode == "disabled" && effort is not (null or "none")) throw new ArgumentException("源关闭模式与启用强度冲突。");
        if (mode != "disabled" && effort is not ("low" or "medium" or "high")) BedrockConversion.Reject("$reasoning.effort", "unsupported_reasoning_effort");
        if (mode == "adaptive") Loss(doc, "$reasoning.mode", "reasoning_mode_changed");
        if (r.ContainsKey("include") && (mode != "disabled" || Boolean(r["include"]))) Loss(doc, "$reasoning.include", "unsupported_reasoning_output");
        if (effort == "high" && (new[] { "maxTokens", "temperature", "topP" }.Any(key => target["inferenceConfig"]?.AsObject().ContainsKey(key) == true) || fields.ContainsKey("topK")))
            throw new ArgumentException("Nova2 high 不能指定 maxTokens/temperature/topP/topK；转换不删除任何控制。");
        var reasoning = new JsonObject { ["type"] = mode == "disabled" ? "disabled" : "enabled" };
        if (mode != "disabled") reasoning["maxReasoningEffort"] = effort; fields["reasoningConfig"] = reasoning;
    }
    /// <summary>显式 null 不是省略，避免以缺省强度替换调用者提供的原始状态。</summary>
    private static string? Effort(JsonObject r) => r.ContainsKey("effort") ? Text(r["effort"]) : null;
}
