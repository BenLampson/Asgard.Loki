using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>模型相关语义保持显式；不伪造定性强度、预算或思考摘要详细程度。</summary>
public sealed class ReasoningIntentTests
{
    /// <summary>调用者配置不能无声覆盖源预算或关闭模式。</summary>
    [Theory]
    [InlineData(0)] [InlineData(2048)]
    public void ConflictingTargetBudget_IsRejected(int sourceBudget)
    {
        var source = Request(LlmProtocol.Gemini);
        source["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = sourceBudget } };
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages,
            new() { TargetThinkingBudgetTokens = 3072 }));
    }

    /// <summary>预算与原有强度都被替代时，两个语义变化分别诊断。</summary>
    [Fact]
    public void ExplicitEffortReplacement_DiagnosesBothChanges()
    {
        var source = Request(LlmProtocol.ClaudeMessages); SetEffort(source, LlmProtocol.ClaudeMessages, "low");
        source["thinking"] = JsonNode.Parse("""{"type":"enabled","budget_tokens":2048}""");
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses,
            new() { TargetReasoningEffort = "high", AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Code == "reasoning_budget_replaced");
        Assert.Contains(result.Diagnostics, d => d.Code == "reasoning_effort_replaced");
    }

    /// <summary>关闭推理使用各自明确的原生关闭字段。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void NoneEffort_DisablesThinkingExplicitly(LlmProtocol to)
    {
        var source = Request(LlmProtocol.ChatCompletions); SetEffort(source, LlmProtocol.ChatCompletions, "none");
        var target = Convert(source, LlmProtocol.ChatCompletions, to);
        if (to == LlmProtocol.ClaudeMessages) Assert.Equal("disabled", target["thinking"]!["type"]!.GetValue<string>());
        else Assert.Equal(0, target["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }

    /// <summary>动态预算映射为动态模式而不是猜一个数值预算。</summary>
    [Fact]
    public void DynamicBudget_MapsAdaptiveMode()
    {
        var source = Request(LlmProtocol.Gemini); source["generationConfig"] = JsonNode.Parse("""{"thinkingConfig":{"thinkingBudget":-1}}""");
        var target = Convert(source, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages);
        Assert.Equal("adaptive", target["thinking"]!["type"]!.GetValue<string>());
        Assert.False(target["thinking"]!.AsObject().ContainsKey("budget_tokens"));
        var back = Convert(target, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini);
        Assert.Equal(-1, back["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }

    /// <summary>返回摘要的意图与 reasoning effort 独立，不能互相覆盖。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void SummaryIntent_IsPreserved(LlmProtocol to)
    {
        var source = Request(LlmProtocol.Responses); source["reasoning"] = JsonNode.Parse("""{"effort":"medium","summary":"auto"}""");
        var target = Convert(source, LlmProtocol.Responses, to);
        Assert.Equal("medium", Effort(target, to).ToLowerInvariant());
        if (to == LlmProtocol.ClaudeMessages) Assert.Equal("summarized", target["thinking"]!["display"]!.GetValue<string>());
        else Assert.True(target["generationConfig"]!["thinkingConfig"]!["includeThoughts"]!.GetValue<bool>());
    }

    /// <summary>目标无法表示摘要详细程度时继续严格拒绝，而不是把 detailed 说成无损 auto。</summary>
    [Fact]
    public void SummaryDetail_RequiresLossConsent()
    {
        var source = Request(LlmProtocol.Responses); source["reasoning"] = JsonNode.Parse("""{"effort":"medium","summary":"detailed"}""");
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.Responses, LlmProtocol.Gemini));
        Assert.Contains(failure.Diagnostics, d => d.Code == "reasoning_summary_detail_changed");
    }

    /// <summary>源协议已经同时约束 effort 和预算时，Gemini 不能悄悄丢失其中一个。</summary>
    [Fact]
    public void EffortAndBudget_AreNotSilentlyCollapsed()
    {
        var source = Request(LlmProtocol.ClaudeMessages); SetEffort(source, LlmProtocol.ClaudeMessages, "low");
        source["thinking"] = JsonNode.Parse("""{"type":"enabled","budget_tokens":2048}""");
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini));
        Assert.Contains(failure.Diagnostics, d => d.Code == "reasoning_effort_not_representable");
    }

    /// <summary>OpenAI 两协议之间的 null effort 仍是 null，不会变成推理关闭。</summary>
    [Fact]
    public void NullEffort_RemainsNullInOpenAi()
    {
        var source = Request(LlmProtocol.ChatCompletions); source["reasoning_effort"] = null;
        var target = Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        Assert.True(target["reasoning"]!.AsObject().ContainsKey("effort")); Assert.Null(target["reasoning"]!["effort"]);
    }

    /// <summary>原始名、描述和 strict=false 在不接受它们的目标上必须有字段级诊断。</summary>
    [Fact]
    public void SchemaWrapperMetadata_IsDiagnosed()
    {
        var source = Request(LlmProtocol.ChatCompletions); SetFormat(source, LlmProtocol.ChatCompletions, JsonNode.Parse("""{"type":"object"}""")!);
        source["response_format"]!["json_schema"]!["description"] = "Do not omit this contract";
        source["response_format"]!["json_schema"]!["strict"] = false;
        var result = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, new() { AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Path == "$format.name");
        Assert.Contains(result.Diagnostics, d => d.Path == "$format.description");
        Assert.Contains(result.Diagnostics, d => d.Code == "schema_enforcement_changed");
    }
}
