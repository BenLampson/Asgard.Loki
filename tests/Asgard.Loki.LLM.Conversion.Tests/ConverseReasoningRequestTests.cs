using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立完整金样验证 Converse 模型方言、严格 Schema 和显式预算替代，不通过生产写出器构造预期值。</summary>
public sealed class ConverseReasoningRequestTests
{
    /// <summary>名称来自预先提交的八组原始请求/完整目标。</summary>
    public static IEnumerable<object[]> Cases() => Samples().Select(node => new object[] { node!["name"]!.GetValue<string>() });
    /// <summary>模型参数位置、推理意图、输出 Schema、数值和原文均逐字段比对。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public void ExplicitDialect_MatchesIndependentCompleteRequest(string name)
    {
        var sample = Sample(name); var protocol = Enum.Parse<LlmProtocol>(sample["protocol"]!.GetValue<string>());
        var options = Options(sample); var source = sample["source"]!.AsObject(); var original = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, protocol, LlmProtocol.BedrockConverse, options);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(sample["expected"]), result.Data), result.Data.GetRawText());
        Assert.Equal(original, source.ToJsonString()); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source), result.OriginalData));
        Assert.Equal(sample["diagnostics"]!.AsArray().Select(item => (item!["code"]!.GetValue<string>(), item["path"]!.GetValue<string>())), result.Diagnostics.Select(item => (item.Code, item.Path)));
        Assert.NotNull(result.Context);
    }

    /// <summary>现有默认绑定序列化不因新增 opt-in 属性改变，未定义方言也不能落到任意分支。</summary>
    [Fact]
    public void Dialect_IsExplicitAndDefaultSerializationCompatible()
    {
        Assert.DoesNotContain("BedrockConverseModelDialect", JsonSerializer.Serialize(new LlmConversionOptions()));
        var sample = Sample("anthropic-chat-low"); var source = sample["source"]!.AsObject();
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions,
            LlmProtocol.BedrockConverse, new() { BedrockConverseModelDialect = (BedrockConverseModelDialect)123 }));
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions,
            LlmProtocol.BedrockConverse, new() { AllowLoss = true }));
        Assert.Equal("converse_model_dialect_required", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>输出上限、采样冲突、预算和显示策略不能通过损耗许可或自动删字段获得成功。</summary>
    [Theory] [InlineData("anthropic-chat-low", "temperature")] [InlineData("anthropic-chat-low", "minimal")]
    [InlineData("anthropic-chat-low", "layout")] [InlineData("anthropic-claude-budget", "budget-limit")]
    [InlineData("anthropic-claude-budget", "budget-conflict")] [InlineData("anthropic-gemini-off", "show-disabled")]
    [InlineData("anthropic-claude-budget", "unknown-mode")] [InlineData("anthropic-claude-budget", "null-display")]
    [InlineData("nova2-chat-high-direct", "temperature")] [InlineData("nova2-chat-high-direct", "max-limit")]
    [InlineData("nova2-chat-high-direct", "layout")] [InlineData("nova2-claude-off-topk", "topk-limit")]
    [InlineData("nova2-gemini-explicit-budget-replacement", "no-replacement")]
    [InlineData("nova2-gemini-explicit-budget-replacement", "no-loss-consent")]
    public void ExecutionConflicts_FailWithoutMutatingRequest(string name, string kind)
    {
        var sample = Sample(name); var source = sample["source"]!.AsObject(); var options = Options(sample) with { AllowLoss = true };
        if (kind == "temperature") source["temperature"] = 0;
        if (kind == "minimal") source["reasoning_effort"] = "minimal";
        if (kind == "layout") options = options with { BedrockConverseInferenceFormat = name.StartsWith("anthropic", StringComparison.Ordinal)
            ? BedrockConverseInferenceFormat.Direct : BedrockConverseInferenceFormat.Unspecified };
        if (kind == "budget-limit") source["max_tokens"] = 2048;
        if (kind == "budget-conflict") options = options with { TargetThinkingBudgetTokens = 4096 };
        if (kind == "unknown-mode") source["thinking"]!["type"] = "future_mode";
        if (kind == "null-display") source["thinking"]!["display"] = null;
        if (kind == "show-disabled") source["generationConfig"]!["thinkingConfig"]!["includeThoughts"] = true;
        if (kind == "max-limit") source["max_completion_tokens"] = 8192;
        if (kind == "topk-limit") source["top_k"] = 129;
        if (kind == "no-replacement") options = options with { TargetReasoningEffort = null };
        if (kind == "no-loss-consent") options = options with { AllowLoss = false };
        var original = source.ToJsonString();
        Assert.NotNull(Record.Exception(() => LlmProtocolConverter.ConvertRequest(source,
            Enum.Parse<LlmProtocol>(sample["protocol"]!.GetValue<string>()), LlmProtocol.BedrockConverse, options)));
        Assert.Equal(original, source.ToJsonString());
    }

    /// <summary>请求设置支持不能被理解为已经允许丢弃原生签名或推理内容。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public void ReasoningResponseRemainsAnExplicitBoundary(string name)
    {
        var sample = Sample(name); var request = LlmProtocolConverter.ConvertRequest(sample["source"]!.AsObject(),
            Enum.Parse<LlmProtocol>(sample["protocol"]!.GetValue<string>()), LlmProtocol.BedrockConverse, Options(sample) with { AllowLoss = true });
        var response = ConverseTextFixtures.Read(LlmProtocol.ChatCompletions, "nativeResponse");
        response["output"]!["message"]!["content"]!.AsArray().Insert(0, new JsonObject
            { ["reasoningContent"] = new JsonObject { ["reasoningText"] = new JsonObject { ["text"] = "thinking", ["signature"] = "opaque" } } });
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(response, request.Context!));
        Assert.Equal("converse_content_not_representable", Assert.Single(error.Diagnostics).Code);
    }
    /// <summary>只读取测试资源，不从实现结果导出预期值。</summary>
    private static JsonArray Samples()
    {
        using var stream = typeof(ConverseReasoningRequestTests).Assembly.GetManifestResourceStream("ConverseReasoningRequests.Fixture")!;
        return JsonNode.Parse(stream)!["cases"]!.AsArray();
    }
    /// <summary>每次取得独立对象，变体不能污染其他用例。</summary>
    private static JsonObject Sample(string name) => Samples().Single(node => node!["name"]!.GetValue<string>() == name)!.AsObject();
    /// <summary>金样以可审阅名称声明枚举，不在生产代码解析模型字符串。</summary>
    private static LlmConversionOptions Options(JsonObject sample) => sample["options"]!.Deserialize<LlmConversionOptions>(new JsonSerializerOptions
        { Converters = { new JsonStringEnumConverter() } })!;
}
