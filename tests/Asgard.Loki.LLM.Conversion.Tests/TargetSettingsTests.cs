using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>调用者声明约束实际转换后的目标，不猜模型能力、不修改字段或覆盖协议限制。</summary>
public sealed class TargetSettingsTests
{
    /// <summary>十二个方向的实际强度均可被允许或硬拒绝，源请求保持不变。</summary>
    [Theory]
    [MemberData(nameof(Directions), MemberType = typeof(GenerationSettingsTests))]
    public void Effort_IsCheckedAfterConversion(LlmProtocol from, LlmProtocol to)
    {
        var source = Request(from); SetEffort(source, from, "medium"); var before = source.ToJsonString();
        var allowed = new LlmConversionOptions { TargetSettings = new() { AllowedReasoningEfforts = ["medium"] } };
        Assert.Equal("medium", Effort(Convert(source, from, to, allowed), to).ToLowerInvariant());
        var denied = allowed with { AllowLoss = true, TargetSettings = new() { AllowedReasoningEfforts = ["low"] } };
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, from, to, denied));
        Assert.Equal("target_reasoning_effort_not_supported", Assert.Single(failure.Diagnostics).Code);
        Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>数值预算显式替代后的最终 effort 也必须符合声明，不能只检查源模式。</summary>
    [Fact]
    public void ExplicitReplacement_IsCheckedOnFinalWire()
    {
        var source = Request(LlmProtocol.ClaudeMessages); source["thinking"] = JsonNode.Parse("""{"type":"enabled","budget_tokens":2048}""");
        var options = new LlmConversionOptions { AllowLoss = true, TargetReasoningEffort = "high", TargetSettings = new() { AllowedReasoningEfforts = ["low"] } };
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, options));
        Assert.Equal("$.reasoning.effort", Assert.Single(failure.Diagnostics).Path);
        var allowed = options with { TargetSettings = new() { AllowedReasoningEfforts = ["high"] } };
        Assert.Equal("high", Effort(Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses, allowed), LlmProtocol.Responses));
    }

    /// <summary>关闭、动态和手动预算是不同模式，不能通过改预算满足声明。</summary>
    [Theory]
    [InlineData(-1, "adaptive")] [InlineData(0, "disabled")] [InlineData(2048, "manual")]
    public void NativeThinkingMode_IsDeclared(int budget, string mode)
    {
        foreach (var from in new[] { LlmProtocol.Gemini, LlmProtocol.ClaudeMessages })
        {
            var target = from == LlmProtocol.Gemini ? LlmProtocol.ClaudeMessages : LlmProtocol.Gemini;
            var source = ThinkingRequest(budget, from);
            var accepted = new LlmConversionOptions { TargetSettings = new() { AllowedThinkingModes = [mode] } };
            _ = Convert(source, from, target, accepted);
            var rejected = accepted with { AllowLoss = true, TargetSettings = new() { AllowedThinkingModes = [] } };
            var failure = Assert.Throws<LlmConversionException>(() => Convert(source, from, target, rejected));
            Assert.Equal("target_thinking_mode_not_supported", Assert.Single(failure.Diagnostics).Code);
        }
    }

    /// <summary>正数预算按声明上下界检查；零和 -1 不变成最小手动预算。</summary>
    [Theory]
    [InlineData(2048, 1024, 2048, true)] [InlineData(2048, 4096, 8192, false)]
    [InlineData(2048, 1024, 1024, false)] [InlineData(0, 4096, 8192, true)] [InlineData(-1, 4096, 8192, true)]
    public void ManualBudgetRange_DoesNotClampModes(int budget, int minimum, int maximum, bool accepted)
    {
        foreach (var from in new[] { LlmProtocol.Gemini, LlmProtocol.ClaudeMessages })
        {
            var target = from == LlmProtocol.Gemini ? LlmProtocol.ClaudeMessages : LlmProtocol.Gemini;
            var source = ThinkingRequest(budget, from); var before = source.ToJsonString();
            var options = new LlmConversionOptions { AllowLoss = true, TargetSettings = new() { MinimumThinkingBudgetTokens = minimum, MaximumThinkingBudgetTokens = maximum } };
            if (accepted) _ = Convert(source, from, target, options);
            else Assert.Equal("target_thinking_budget_out_of_range", Assert.Single(Assert.Throws<LlmConversionException>(
                () => Convert(source, from, target, options)).Diagnostics).Code);
            Assert.Equal(before, source.ToJsonString());
        }
    }

    /// <summary>四个目标的最终 token 上限原样保留，超限不能被 AllowLoss 放行。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void OutputLimit_IsValidatedWithoutAdjustment(LlmProtocol target)
    {
        var from = target == LlmProtocol.Responses ? LlmProtocol.ChatCompletions : LlmProtocol.Responses;
        var source = Request(from);
        _ = Convert(source, from, target, new() { TargetSettings = new() { MaximumOutputTokens = 4096 } });
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, from, target,
            new() { AllowLoss = true, TargetSettings = new() { MaximumOutputTokens = 4095 } }));
        Assert.Equal("target_output_limit_exceeded", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>Claude 显式默认上限写入后也要检查，不能绕过声明。</summary>
    [Fact]
    public void CallerDefaultOutputLimit_IsChecked()
        => Assert.Equal("$.max_tokens", Assert.Single(Assert.Throws<LlmConversionException>(() => Convert(Request(LlmProtocol.Gemini),
            LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, new() { DefaultMaxTokens = 4096, TargetSettings = new() { MaximumOutputTokens = 128 } })).Diagnostics).Path);

    /// <summary>未提供上限时不补参数，不声称已控制上游默认输出或计费额度。</summary>
    [Fact]
    public void OmittedOutputLimit_RemainsOmitted()
    {
        var result = Convert(Request(LlmProtocol.Gemini), LlmProtocol.Gemini, LlmProtocol.Responses, new() { TargetSettings = new() { MaximumOutputTokens = 1 } });
        Assert.False(result.ContainsKey("max_output_tokens"));
    }

    /// <summary>声明的正数上限校验不把显式 null/零/负数当成未提供。</summary>
    [Theory]
    [InlineData("null")] [InlineData("0")] [InlineData("-1")]
    public void InvalidExplicitOutputLimits_AreNotOmission(string json)
    {
        var source = Request(LlmProtocol.ChatCompletions); source["max_completion_tokens"] = JsonNode.Parse(json);
        Assert.Throws<ArgumentException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            new() { TargetSettings = new() { MaximumOutputTokens = 128 } }));
    }

    /// <summary>纯文本和 JSON-object 在全部 Gemini 外壳下都保持各自意图，不能冒充 JSON Schema。</summary>
    [Theory]
    [InlineData("text")] [InlineData("json_object")]
    public void PlainOutputIntent_IsCheckedInEveryGeminiEnvelope(string kind)
    {
        foreach (var format in Enum.GetValues<GeminiOutputFormat>())
        {
            var source = Request(LlmProtocol.ChatCompletions); source["response_format"] = new JsonObject { ["type"] = kind };
            var options = new LlmConversionOptions { GeminiOutputFormat = format, TargetSettings = new() { AllowedOutputFormats = [kind] } };
            _ = Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini, options);
            Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Gemini,
                options with { AllowLoss = true, TargetSettings = new() { AllowedOutputFormats = ["json_schema"] } }));
        }
    }

    /// <summary>零、null 和其他显式采样值都不能被删除来适配不支持采样的声明。</summary>
    [Theory]
    [InlineData("0")] [InlineData("null")] [InlineData("1")]
    public void SamplingPresence_IncludesZeroAndNull(string json)
    {
        var source = Request(LlmProtocol.ChatCompletions); source["temperature"] = JsonNode.Parse(json); var before = source.ToJsonString();
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ChatCompletions, LlmProtocol.Responses,
            new() { AllowLoss = true, TargetSettings = new() { SupportsSampling = false } }));
        Assert.Equal("$.temperature", Assert.Single(failure.Diagnostics).Path); Assert.Equal(before, source.ToJsonString());
    }

    /// <summary>明确 false/null 的推理显示控制仍是一个字段，不能当作省略。</summary>
    [Theory]
    [InlineData(LlmProtocol.Responses, "$.reasoning.summary")]
    [InlineData(LlmProtocol.ClaudeMessages, "$.thinking.display")]
    [InlineData(LlmProtocol.Gemini, "$.generationConfig.thinkingConfig.includeThoughts")]
    public void ReasoningOutputPresence_IsNotErased(LlmProtocol target, string path)
    {
        var from = target == LlmProtocol.Gemini ? LlmProtocol.ClaudeMessages : LlmProtocol.Gemini; var source = Request(from);
        if (from == LlmProtocol.Gemini) source["generationConfig"] = JsonNode.Parse("""{"thinkingConfig":{"thinkingBudget":0,"includeThoughts":false}}""");
        else source["thinking"] = JsonNode.Parse("""{"type":"disabled","display":"omitted"}""");
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, from, target,
            new() { AllowLoss = true, TargetSettings = new() { SupportsReasoningOutputControl = false } }));
        Assert.Equal(path, Assert.Single(failure.Diagnostics).Path);
    }

    /// <summary>并行 false 仍要求目标支持控制字段，不能偷偷省略。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.ClaudeMessages)]
    public void ParallelFalse_RequiresDeclaredControlSupport(LlmProtocol target)
    {
        var from = target == LlmProtocol.ChatCompletions ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
        var source = ConversionFixtures.Request(from); source["parallel_tool_calls"] = false;
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, from, target,
            new() { AllowLoss = true, TargetSettings = new() { SupportsParallelToolControl = false } }));
        Assert.Equal("target_setting_not_supported", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>四种目标的 Schema 意图按实际原生格式字段识别。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void OutputFormat_IsCheckedAfterNativeEnvelopeSelection(LlmProtocol target)
    {
        var from = target == LlmProtocol.ClaudeMessages ? LlmProtocol.Gemini : LlmProtocol.ClaudeMessages;
        var source = Request(from); SetFormat(source, from, JsonNode.Parse("""{"type":"object"}""")!);
        var options = new LlmConversionOptions { DefaultSchemaName = "result", TargetSettings = new() { AllowedOutputFormats = ["json_schema"] } };
        _ = Convert(source, from, target, options);
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, from, target,
            options with { AllowLoss = true, TargetSettings = new() { AllowedOutputFormats = ["text"] } }));
        Assert.Equal("target_output_format_not_supported", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>显式允许不能启用转换器或协议本来不能表示的字段。</summary>
    [Fact]
    public void SupportDeclaration_DoesNotEnableUnsupportedProtocolFields()
    {
        var source = Request(LlmProtocol.ClaudeMessages); source["top_k"] = 40;
        Assert.Throws<LlmConversionException>(() => Convert(source, LlmProtocol.ClaudeMessages, LlmProtocol.Responses,
            new() { TargetSettings = new() { SupportsSampling = true } }));
    }

    /// <summary>原生复制与响应的 usage 都不受请求设置声明过滤或额度化。</summary>
    [Fact]
    public void NativeCopiesAndResponses_RemainUnchanged()
    {
        var source = Request(LlmProtocol.ChatCompletions); source["temperature"] = 0;
        var options = new LlmConversionOptions { TargetSettings = new() { SupportsSampling = false, MaximumOutputTokens = 1 } };
        var copy = LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions, LlmProtocol.ChatCompletions, options);
        Assert.Equal(source.ToJsonString(), JsonNode.Parse(copy.Data.GetRawText())!.ToJsonString());
        var response = LlmProtocolConverter.ConvertResponse(ConversionFixtures.Response(LlmProtocol.ChatCompletions), LlmProtocol.ChatCompletions, LlmProtocol.Responses, options);
        Assert.Equal(5, response.Data.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    /// <summary>独立构造两个原生 thinking 方言，不使用转换器生成测试输入。</summary>
    private static JsonObject ThinkingRequest(int budget, LlmProtocol protocol)
    {
        var source = Request(protocol);
        if (protocol == LlmProtocol.Gemini) source["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget } };
        else
        {
            var thinking = new JsonObject { ["type"] = budget == 0 ? "disabled" : budget == -1 ? "adaptive" : "enabled" };
            if (budget > 0) thinking["budget_tokens"] = budget;
            source["thinking"] = thinking;
        }
        return source;
    }
}
