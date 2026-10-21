using Asgard.Loki.LLM.OpenAI;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>支持声明是调用者配置，不是可变模型表；快照与绑定身份必须稳定。</summary>
public sealed class TargetSettingsConfigurationTests
{
    /// <summary>非法列表值、非正范围与倒置区间在构造供应商或流状态前失败。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void InvalidConfiguration_IsRejectedEagerly(int variant)
    {
        var settings = variant switch
        {
            0 => new LlmConversionTargetSettings { AllowedReasoningEfforts = ["future"] },
            1 => new LlmConversionTargetSettings { AllowedThinkingModes = ["auto"] },
            2 => new LlmConversionTargetSettings { AllowedOutputFormats = ["protobuf"] },
            3 => new LlmConversionTargetSettings { MaximumOutputTokens = 0 },
            4 => new LlmConversionTargetSettings { MinimumThinkingBudgetTokens = 0 },
            5 => new LlmConversionTargetSettings { MaximumThinkingBudgetTokens = 0 },
            _ => new LlmConversionTargetSettings { MinimumThinkingBudgetTokens = 2048, MaximumThinkingBudgetTokens = 1024 }
        };
        var options = new LlmConversionOptions { TargetSettings = settings };
        Assert.ThrowsAny<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(Request(LlmProtocol.ChatCompletions), LlmProtocol.ChatCompletions, LlmProtocol.Responses, options));
        Assert.ThrowsAny<ArgumentException>(() => new LlmProtocolStreamConverter(LlmProtocol.ChatCompletions, LlmProtocol.Responses, options));
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.ThrowsAny<ArgumentException>(() => new ConvertingLlmProvider(new OpenAiLlmProvider(new StubHttpClientFactory(handler)), LlmProtocol.Responses, "wrapper", options));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>空列表是明确不支持，不同于未提供声明；none 与显式 null 都不是省略。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyList_IsNotAnUnspecifiedDeclaration(bool nullEffort)
    {
        var source = Request(LlmProtocol.ChatCompletions); source["reasoning_effort"] = nullEffort ? null : "none";
        var unrestricted = Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses, new() { TargetSettings = new() });
        Assert.True(unrestricted["reasoning"]!.AsObject().ContainsKey("effort"));
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            new() { AllowLoss = true, TargetSettings = new() { AllowedReasoningEfforts = [] } }));
    }

    /// <summary>注册后修改原始三个列表，不能改变绑定身份或已保存的支持声明。</summary>
    [Fact]
    public void MutableCollections_AreSnapshotted()
    {
        List<string> efforts = ["medium"]; List<string> modes = ["manual"]; List<string> formats = ["json_schema"];
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var provider = new ConvertingLlmProvider(new OpenAiLlmProvider(new StubHttpClientFactory(handler)), LlmProtocol.Responses, "wrapper", new()
        { TargetSettings = new() { AllowedReasoningEfforts = efforts, AllowedThinkingModes = modes, AllowedOutputFormats = formats } });
        var before = provider.BindingIdentity;
        efforts[0] = "low"; modes[0] = "disabled"; formats[0] = "text";
        Assert.Equal(before, provider.BindingIdentity);
    }

    /// <summary>声明列表按集合语义处理；重排和重复值不能无故改变同一目标的绑定。</summary>
    [Fact]
    public void EquivalentSets_HaveStableIdentity()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var native = new OpenAiLlmProvider(new StubHttpClientFactory(handler));
        var first = new ConvertingLlmProvider(native, LlmProtocol.Responses, "wrapper", new()
        { TargetSettings = new() { AllowedReasoningEfforts = ["medium", "high"], AllowedOutputFormats = ["text", "json_schema"] } });
        var second = new ConvertingLlmProvider(native, LlmProtocol.Responses, "wrapper", new()
        { TargetSettings = new() { AllowedReasoningEfforts = ["high", "medium", "medium"], AllowedOutputFormats = ["json_schema", "text"] } });
        Assert.Equal(first.BindingIdentity, second.BindingIdentity);
    }

    /// <summary>目标显式手动预算配置在写入后也受范围限制，不能绕过源请求没有预算的情况。</summary>
    [Fact]
    public void ConfiguredManualBudget_IsValidatedAfterSelection()
    {
        var source = Request(LlmProtocol.ChatCompletions); SetEffort(source, LlmProtocol.ChatCompletions, "medium");
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, new()
        { TargetThinkingBudgetTokens = 2048, TargetSettings = new() { MaximumThinkingBudgetTokens = 1024 } }));
        Assert.Equal("$.thinking.budget_tokens", Assert.Single(failure.Diagnostics).Path);
    }

    /// <summary>同一声明可以报告多个实际字段违反，不能在第一个字段失败后悄悄清洗其余参数。</summary>
    [Fact]
    public void MultipleViolations_AreReportedTogether()
    {
        var source = Request(LlmProtocol.ChatCompletions); source["temperature"] = 0; source["top_p"] = 1;
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini,
            new() { AllowLoss = true, TargetSettings = new() { SupportsSampling = false } }));
        Assert.Equal(new[] { "$.generationConfig.temperature", "$.generationConfig.topP" }, failure.Diagnostics.Select(item => item.Path));
    }

    /// <summary>Gemini 三种目标格式外壳均校验相同的 Schema 意图，不能通过切换外壳绕过。</summary>
    [Theory]
    [InlineData(GeminiOutputFormat.LegacyJsonSchema)] [InlineData(GeminiOutputFormat.ResponseFormat)] [InlineData(GeminiOutputFormat.LegacySchema)]
    public void GeminiEnvelopes_UseTheSameDeclaredOutputIntent(GeminiOutputFormat format)
    {
        var source = Request(LlmProtocol.ClaudeMessages); SetFormat(source, LlmProtocol.ClaudeMessages, System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object"}""")!);
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini,
            new() { GeminiOutputFormat = format, AllowLoss = true, TargetSettings = new() { AllowedOutputFormats = [] } }));
    }
}
