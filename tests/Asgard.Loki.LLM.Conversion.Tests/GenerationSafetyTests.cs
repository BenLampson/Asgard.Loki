using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>配置不能靠空值、预算猜测、未知字段或并行开关变成另一种请求语义。</summary>
public sealed class GenerationSafetyTests
{
    /// <summary>畸形的原生结构必须明确失败，不接受隐式 false 或另一种 Schema 方言。</summary>
    [Theory]
    [InlineData(LlmProtocol.Gemini, """{"thinkingConfig":{"thinkingBudget":null}}""")]
    [InlineData(LlmProtocol.Gemini, """{"thinkingConfig":{"includeThoughts":null}}""")]
    [InlineData(LlmProtocol.Gemini, """{"thinkingConfig":{"thinkingBudget":2048,"thinkingLevel":"HIGH"}}""")]
    [InlineData(LlmProtocol.Gemini, """{"responseJsonSchema":{"type":"object"}}""")]
    [InlineData(LlmProtocol.Gemini, """{"responseMimeType":"application/json","responseJsonSchema":{},"responseSchema":{}}""")]
    [InlineData(LlmProtocol.ClaudeMessages, """{"type":"enabled"}""")]
    [InlineData(LlmProtocol.ClaudeMessages, """{"type":"enabled","budget_tokens":512}""")]
    [InlineData(LlmProtocol.ClaudeMessages, """{"type":"adaptive","budget_tokens":2048}""")]
    public void InvalidSettings_AreRejected(LlmProtocol from, string settings)
    {
        var source = Request(from); source[from == LlmProtocol.Gemini ? "generationConfig" : "thinking"] = JsonNode.Parse(settings);
        Assert.Throws<ArgumentException>(() => Convert(source, from, LlmProtocol.Responses));
    }

    /// <summary>已知配置对象的未知字段也形成诊断，显式损耗不能伪称无损。</summary>
    [Theory]
    [InlineData(LlmProtocol.Gemini, """{"thinkingConfig":{"thinkingLevel":"LOW","future":false}}""")]
    [InlineData(LlmProtocol.Gemini, """{"responseMimeType":"application/json","responseSchema":{"type":"OBJECT"}}""")]
    [InlineData(LlmProtocol.ClaudeMessages, """{"effort":"low","future":null}""")]
    public void UnsupportedSettings_AreDiagnosed(LlmProtocol from, string settings)
    {
        var source = Request(from); source[from == LlmProtocol.Gemini ? "generationConfig" : "output_config"] = JsonNode.Parse(settings);
        Assert.Throws<LlmConversionException>(() => Convert(source, from, LlmProtocol.Responses));
        var result = LlmProtocolConverter.ConvertRequest(source, from, LlmProtocol.Responses, new() { AllowLoss = true });
        Assert.NotEmpty(result.Diagnostics);
    }

    /// <summary>明确不可表达的强度不应被钳制到其他强度。</summary>
    [Theory]
    [InlineData("minimal", LlmProtocol.ClaudeMessages)]
    [InlineData("xhigh", LlmProtocol.Gemini)]
    [InlineData("max", LlmProtocol.Gemini)]
    public void UnsupportedEffort_IsNotClamped(string effort, LlmProtocol to)
    {
        var source = Request(LlmProtocol.ChatCompletions); SetEffort(source, LlmProtocol.ChatCompletions, effort);
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, to));
        Assert.Contains(failure.Diagnostics, d => d.Code == "unsupported_reasoning_effort");
    }

    /// <summary>格式和推理共享 output_config/generationConfig，但不能互相覆盖。</summary>
    [Theory]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void FormatAndReasoning_Coexist(LlmProtocol to)
    {
        var source = Request(LlmProtocol.ChatCompletions); SetEffort(source, LlmProtocol.ChatCompletions, "medium");
        SetFormat(source, LlmProtocol.ChatCompletions, JsonNode.Parse("""{"type":"object"}""")!);
        var target = Convert(source, LlmProtocol.ChatCompletions, to, new() { AllowLoss = true });
        Assert.Equal("medium", Effort(target, to).ToLowerInvariant());
        Assert.Equal("object", Schema(target, to)["type"]!.GetValue<string>());
    }

    /// <summary>OpenAI 的显式 null summary 代表不请求摘要，不能变成默认显示思考。</summary>
    [Fact]
    public void NullSummary_PreservesOmittedIntent()
    {
        var source = Request(LlmProtocol.Responses); source["reasoning"] = JsonNode.Parse("""{"effort":"medium","summary":null}""");
        var target = Convert(source, LlmProtocol.Responses, LlmProtocol.ClaudeMessages);
        Assert.Equal("omitted", target["thinking"]!["display"]!.GetValue<string>());
    }

    /// <summary>数值预算不满足目标要求时不修改预算或输出上限。</summary>
    [Fact]
    public void GeminiSmallBudget_IsNotRaisedForClaude()
    {
        var source = Request(LlmProtocol.Gemini); source["generationConfig"] = JsonNode.Parse("""{"thinkingConfig":{"thinkingBudget":512}}""");
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages));
        Assert.Equal(512, source["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }

    /// <summary>并行 false 必须反转为 Claude disable_parallel_tool_use=true，不能删除工具。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ParallelCalls_PreserveExplicitBoolean(bool enabled)
    {
        var source = ConversionFixtures.Request(LlmProtocol.ChatCompletions); source["parallel_tool_calls"] = enabled;
        var target = Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages);
        Assert.Equal(!enabled, target["tool_choice"]!["disable_parallel_tool_use"]!.GetValue<bool>());
        var back = Convert(target, LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        Assert.Equal(enabled, back["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Single(back["tools"]!.AsArray());
    }

    /// <summary>Gemini 缺少等价并行开关，必须诊断；null 不可当成 false。</summary>
    [Fact]
    public void ParallelCalls_RejectUnsupportedTargetAndNull()
    {
        var source = ConversionFixtures.Request(LlmProtocol.ChatCompletions); source["parallel_tool_calls"] = false;
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini));
        source["parallel_tool_calls"] = null;
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses));
    }

    /// <summary>描述性信封的整数数值按 JSON 读取，不能依赖 CLR 装箱类型。</summary>
    [Fact]
    public void ResponseEnvelope_AcceptsIntegerEncodedSamplingMetadata()
    {
        var source = ConversionFixtures.Response(LlmProtocol.Responses); source["temperature"] = 0; source["top_p"] = 1;
        var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.Responses, LlmProtocol.ChatCompletions);
        Assert.Empty(result.Diagnostics); Assert.Equal(0, result.OriginalData.GetProperty("temperature").GetInt32());
    }
}
