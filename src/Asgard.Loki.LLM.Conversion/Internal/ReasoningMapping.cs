using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;
using static Asgard.Loki.LLM.Conversion.Internal.StructuredOutputMapping;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>定性强度、数值预算和输出意图独立转换；不推算预算或调整输出上限。</summary>
internal static class ReasoningMapping
{
    /// <summary>读取原生设置，不解析模型名称或推理内容。</summary>
    internal static void Read(JsonObject source, LlmProtocol protocol, ConversionDocument doc)
    {
        var result = doc.Reasoning;
        if (protocol == LlmProtocol.ChatCompletions) Copy(source, "reasoning_effort", result, "effort");
        if (protocol == LlmProtocol.Responses && source.ContainsKey("reasoning"))
        {
            var reasoning = Object(source["reasoning"]); Check(reasoning, doc, "$.reasoning", "effort", "summary");
            Copy(reasoning, "effort", result, "effort"); Copy(reasoning, "summary", result, "summary");
            if (reasoning.ContainsKey("summary")) result["include"] = reasoning["summary"] is not null;
            if (reasoning["summary"] is not null)
            {
                var summary = Text(reasoning["summary"]);
                if (summary is not ("auto" or "concise" or "detailed")) throw new ArgumentException("未知的推理摘要模式。");
                result["include"] = true;
            }
        }
        if (protocol == LlmProtocol.ClaudeMessages)
        {
            if (source["output_config"] is JsonObject output) Copy(output, "effort", result, "effort");
            if (source.ContainsKey("thinking"))
            {
                var thinking = Object(source["thinking"]); Check(thinking, doc, "$.thinking", "type", "budget_tokens", "display");
                var mode = Text(thinking["type"]);
                if (mode is not ("enabled" or "adaptive" or "disabled"))
                {
                    Loss(doc, "$.thinking.type", "unsupported_thinking_mode"); ValidateEffort(result); return;
                }
                result["mode"] = mode;
                if (mode == "enabled")
                {
                    if (thinking["budget_tokens"] is null || Number(thinking["budget_tokens"]) < 1024 || Number(thinking["budget_tokens"]) > int.MaxValue)
                        throw new ArgumentException("Claude enabled thinking 需要至少 1024 的显式预算。");
                    result["budget"] = Number(thinking["budget_tokens"]);
                }
                else if (thinking.ContainsKey("budget_tokens")) throw new ArgumentException("只有 enabled thinking 可以指定预算。");
                if (thinking["display"] is not null)
                {
                    var display = Text(thinking["display"]);
                    if (display is not ("summarized" or "omitted")) throw new ArgumentException("未知的思考显示模式。");
                    result["include"] = display == "summarized";
                }
            }
        }
        if (protocol == LlmProtocol.Gemini && source["generationConfig"] is JsonObject config && config.ContainsKey("thinkingConfig"))
        {
            var thinking = Object(config["thinkingConfig"]); Check(thinking, doc, "$.generationConfig.thinkingConfig", "thinkingBudget", "thinkingLevel", "includeThoughts");
            if (thinking.ContainsKey("thinkingBudget") && thinking.ContainsKey("thinkingLevel")) throw new ArgumentException("不能同时指定 Gemini budget 和 level。");
            if (thinking.ContainsKey("thinkingLevel")) result["effort"] = Text(thinking["thinkingLevel"]).ToLowerInvariant();
            if (thinking.ContainsKey("thinkingBudget"))
            {
                if (thinking["thinkingBudget"] is null) throw new ArgumentException("Gemini thinkingBudget 不能为 null。");
                var budget = Number(thinking["thinkingBudget"]);
                if (budget < -1 || budget > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(source));
                result["mode"] = budget == 0 ? "disabled" : budget == -1 ? "adaptive" : "enabled";
                if (budget > 0) result["budget"] = budget;
            }
            if (thinking.ContainsKey("includeThoughts")) result["include"] = Boolean(thinking["includeThoughts"]);
        }
        ValidateEffort(result);
        if (result["budget"] is not null && Number(result["budget"]) <= 0) throw new ArgumentOutOfRangeException(nameof(source));
    }

    /// <summary>使用目标原生控制；定性强度不声称在不同模型上有相同消耗或质量。</summary>
    internal static void Write(JsonObject target, LlmProtocol protocol, ConversionDocument doc, LlmConversionOptions options)
    {
        var r = doc.Reasoning; if (r.Count == 0) return;
        var effort = r["effort"]?.GetValue<string>(); var mode = r["mode"]?.GetValue<string>();
        var budget = r["budget"] is null ? (long?)null : Number(r["budget"]);
        if (effort == "none") mode = "disabled";
        if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses)
        {
            if (budget is not null)
            {
                Loss(doc, "$reasoning.budget", options.TargetReasoningEffort is null ? "unsupported_reasoning_budget" : "reasoning_budget_replaced");
                if (options.TargetReasoningEffort is not null)
                {
                    if (effort is not null && effort != options.TargetReasoningEffort) Loss(doc, "$reasoning.effort", "reasoning_effort_replaced");
                    effort = options.TargetReasoningEffort;
                }
            }
            if (mode == "disabled")
            {
                if (effort is not (null or "none")) Loss(doc, "$reasoning.mode", "reasoning_mode_conflict");
                effort = "none";
            }
            else if (mode is not null && effort is null && budget is null) Loss(doc, "$reasoning.mode", "unsupported_thinking_mode");
            if (effort is not null || r.ContainsKey("effort"))
            {
                if (protocol == LlmProtocol.ChatCompletions) target["reasoning_effort"] = effort;
                else Ensure(target, "reasoning")["effort"] = effort;
            }
            if (protocol == LlmProtocol.Responses)
            {
                if (r.ContainsKey("summary")) Ensure(target, "reasoning")["summary"] = r["summary"]?.DeepClone();
                else if (r["include"] is not null) Ensure(target, "reasoning")["summary"] = r["include"]!.GetValue<bool>() ? JsonValue.Create("auto") : null;
            }
            else if (r.ContainsKey("include") || r.ContainsKey("summary")) Loss(doc, "$reasoning.include", "unsupported_reasoning_output");
            return;
        }
        if (r["summary"] is not null && Text(r["summary"]) != "auto") Loss(doc, "$reasoning.summary", "reasoning_summary_detail_changed");
        if (r.ContainsKey("effort") && effort is null) Loss(doc, "$reasoning.effort");
        if (protocol == LlmProtocol.Gemini)
        {
            var thinking = Ensure(Ensure(target, "generationConfig"), "thinkingConfig");
            if (budget is not null) thinking["thinkingBudget"] = budget;
            else if (mode is not null) thinking["thinkingBudget"] = mode == "disabled" ? 0 : -1;
            else if (effort is "minimal" or "low" or "medium" or "high") thinking["thinkingLevel"] = effort.ToUpperInvariant();
            else if (effort is not null) Loss(doc, "$reasoning.effort", "unsupported_reasoning_effort");
            if (mode is not null && effort is not (null or "none")) Loss(doc, "$reasoning.effort", "reasoning_effort_not_representable");
            Copy(r, "include", thinking, "includeThoughts");
            return;
        }
        if (effort is not (null or "none"))
        {
            if (effort == "minimal") Loss(doc, "$reasoning.effort", "unsupported_reasoning_effort");
            else Ensure(target, "output_config")["effort"] = effort;
        }
        if (options.TargetThinkingBudgetTokens is { } configuredBudget
            && (mode == "disabled" || budget is { } sourceBudget && sourceBudget != configuredBudget))
            throw new ArgumentException("目标手动预算与源关闭模式或显式预算冲突。");
        var selectedBudget = budget ?? options.TargetThinkingBudgetTokens;
        if (selectedBudget is not null && mode != "disabled")
        {
            if (selectedBudget < 1024 || selectedBudget >= Number(target["max_tokens"]))
                throw new ArgumentException("Claude 手动预算至少为 1024，且必须小于显式输出上限。");
            if (mode == "adaptive") Loss(doc, "$reasoning.mode", "reasoning_mode_changed");
            target["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = selectedBudget };
        }
        else if (mode is not null || effort is not null)
            target["thinking"] = new JsonObject { ["type"] = mode == "disabled" ? "disabled" : "adaptive" };
        if (r.ContainsKey("include"))
        {
            if (target["thinking"] is JsonObject thinking) thinking["display"] = r["include"]!.GetValue<bool>() ? "summarized" : "omitted";
            else Loss(doc, "$reasoning.include", "thinking_mode_required");
        }
    }

    /// <summary>未知模式的损耗不能掩盖同时存在的非法强度。</summary>
    private static void ValidateEffort(JsonObject reasoning)
    {
        if (reasoning["effort"] is not null && Text(reasoning["effort"]) is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("未知的推理强度。");
    }
}
