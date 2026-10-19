using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>新增完整原生映射不能绕过文件、签名、工具关联、其他协议和未实现流边界。</summary>
public sealed class BedrockConversionBoundaryTests
{
    /// <summary>原生不透明内容和无对应输入均不可通过损耗许可丢弃。</summary>
    public static IEnumerable<object[]> NativeContent()
    {
        foreach (var nova in new[] { false, true })
        foreach (var field in new[] { "document", "reasoningContent", "cachePoint", "guardContent", "citationsContent", "audio", "searchResult" })
            yield return [nova, field];
    }
    /// <summary>Converse 文档不能冒充 Nova 文档，签名/引用/策略状态也不被降级。</summary>
    [Theory] [MemberData(nameof(NativeContent))]
    public void NativeOnlyContent_AlwaysRejects(bool nova, string field)
    {
        var source = Source(nova); source["messages"]![0]!["content"]!.AsArray().Add(new JsonObject { [field] = new JsonObject { ["encrypted_content"] = "opaque" } });
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, nova, true));
        Assert.Equal("bedrock_native_only", Assert.Single(failure.Diagnostics).Code); Assert.DoesNotContain("opaque", failure.Message);
    }

    /// <summary>输出结构化约束和原生安全策略不能因当前缺少等价映射而被静默取消。</summary>
    [Theory] [InlineData(true, "outputConfig")] [InlineData(false, "outputConfig")]
    [InlineData(true, "guardrailConfig")] [InlineData(false, "guardrailConfig")]
    [InlineData(true, "promptVariables")] [InlineData(false, "promptVariables")]
    public void NativeExecutionFields_AreHardBoundaries(bool nova, string field)
    {
        var source = Source(nova); source[field] = new JsonObject();
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, nova, true));
        Assert.Equal("$." + field, Assert.Single(failure.Diagnostics).Path);
    }

    /// <summary>工具错误只来自原生状态字段，普通 JSON 的同名字段不会触发递归检查。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void BusinessJson_IsNotScannedAsProtocolState(bool nova)
    {
        var source = Source(nova); var business = JsonNode.Parse("""{"error":{"type":"response.failed"},"reasoningContent":{"signature":"business"},"encrypted_content":"business"}""")!;
        source["messages"]![1]!["content"]![0]!["toolUse"]!["input"] = business;
        var output = Convert(source, nova, false);
        BedrockConversionTests.Equal(business, output.Data.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("toolUse").GetProperty("input"));
    }

    /// <summary>关联 ID、消息角色和原生联合分支无效时不做猜测或合并。</summary>
    [Theory] [InlineData(true, "duplicate-call")] [InlineData(false, "duplicate-call")]
    [InlineData(true, "orphan")] [InlineData(false, "orphan")]
    [InlineData(true, "duplicate-result")] [InlineData(false, "duplicate-result")]
    [InlineData(true, "call-role")] [InlineData(false, "call-role")]
    [InlineData(true, "result-role")] [InlineData(false, "result-role")]
    [InlineData(true, "union")] [InlineData(false, "union")]
    public void BrokenNativeAssociation_AlwaysRejects(bool nova, string kind)
    {
        var source = Source(nova); var calls = source["messages"]![1]!["content"]!.AsArray(); var results = source["messages"]![2]!["content"]!.AsArray();
        if (kind == "duplicate-call") calls[1]!["toolUse"]!["toolUseId"] = "call-a";
        if (kind == "orphan") results[0]!["toolResult"]!["toolUseId"] = "foreign";
        if (kind == "duplicate-result") results.Add(results[0]!.DeepClone());
        if (kind == "call-role") source["messages"]![1]!["role"] = "user";
        if (kind == "result-role") source["messages"]![2]!["role"] = "assistant";
        if (kind == "union") calls[0]!["text"] = "ambiguous";
        if (kind == "union") Assert.Throws<LlmConversionException>(() => Convert(source, nova, true));
        else Assert.Throws<ArgumentException>(() => Convert(source, nova, true));
    }

    /// <summary>不能把新原生枚举等同于四种既有协议，也不能绕过明确缺少的流状态机。</summary>
    [Theory] [InlineData(LlmProtocol.BedrockNova, false)] [InlineData(LlmProtocol.BedrockNova, true)]
    [InlineData(LlmProtocol.BedrockConverse, false)] [InlineData(LlmProtocol.BedrockConverse, true)]
    public void UnsupportedPairsAndStreams_FailBeforeProcessing(LlmProtocol native, bool allowLoss)
    {
        foreach (var other in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
        foreach (var pair in new[] { (From: native, To: other), (From: other, To: native) })
        {
            Assert.Throws<NotSupportedException>(() => LlmProtocolConverter.ConvertRequest(new(), pair.From, pair.To, new() { AllowLoss = allowLoss }));
            Assert.Throws<NotSupportedException>(() => LlmProtocolConverter.ConvertResponse(new(), pair.From, pair.To, new() { AllowLoss = allowLoss }));
            Assert.Throws<NotSupportedException>(() => new LlmProtocolStreamConverter(pair.From, pair.To, new() { AllowLoss = allowLoss }));
        }
        Assert.Throws<NotSupportedException>(() => new LlmProtocolStreamConverter(native,
            native == LlmProtocol.BedrockNova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, new() { AllowLoss = allowLoss }));
    }

    /// <summary>可选采样/推理缺失时保持缺失，不强加包装、输出上限或默认参数。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void OmittedSettings_RemainOmitted(bool nova)
    {
        var source = Source(nova); source.Remove("inferenceConfig"); source.Remove("additionalModelRequestFields");
        var options = BedrockConversionFixtures.Options() with { BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Unspecified };
        var actual = LlmProtocolConverter.ConvertRequest(source, nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, options);
        Assert.False(actual.Data.TryGetProperty("inferenceConfig", out _)); Assert.False(actual.Data.TryGetProperty("additionalModelRequestFields", out _));
    }

    /// <summary>新增 opt-in 选项只有显式选择时进入序列化身份，非法枚举不能悄悄走缺省。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ExplicitOptions_RejectUndefinedEnums(bool format)
    {
        var options = BedrockConversionFixtures.Options(); options = format ? options with { BedrockNovaRequestFormat = (BedrockNovaRequestFormat)123 }
            : options with { BedrockConverseInferenceFormat = (BedrockConverseInferenceFormat)123 };
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(Source(true), LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse, options));
        var defaults = JsonSerializer.Serialize(new LlmConversionOptions());
        Assert.DoesNotContain("BedrockNovaRequestFormat", defaults); Assert.DoesNotContain("BedrockConverseInferenceFormat", defaults);
    }

    /// <summary>本轮不忽略仅为四协议定义的目标约束。</summary>
    [Fact]
    public void FourProtocolTargetSettings_AreNotSilentlyIgnored()
        => Assert.Throws<NotSupportedException>(() => LlmProtocolConverter.ConvertRequest(Source(true), LlmProtocol.BedrockNova,
            LlmProtocol.BedrockConverse, BedrockConversionFixtures.Options() with { TargetSettings = new() }));

    /// <summary>未证明等价的严格工具约束不能因普通字段损耗许可而取消。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ToolStrictConstraint_IsNotDropped(bool nova)
    {
        var source = Source(nova); source["toolConfig"]!["tools"]![0]!["toolSpec"]!["strict"] = true;
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, nova, true));
        Assert.Equal("$.toolConfig.tools[].toolSpec.strict", Assert.Single(failure.Diagnostics).Path);
    }

    /// <summary>已回填的历史调用 ID 不能被本轮新调用复用，原始上下文以 ID 而非同名工具判定。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void RequestContext_RejectsReusedHistoricalCallId(bool nova)
    {
        var request = Convert(Source(nova), nova, true);
        var response = BedrockConversionFixtures.Read("v2", nova ? "ConverseResponse" : "NovaResponse");
        response["output"]!["message"]!["content"]![1]!["toolUse"]!["toolUseId"] = "call-a";
        var failure = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(response, request.Context!));
        Assert.Equal("tool_call_id_reused", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>所有负例使用完整有效请求变体，避免因缺少其他字段而提前失败。</summary>
    private static JsonObject Source(bool nova) => BedrockConversionFixtures.Read("v2", nova ? "Nova" : "ConverseDirect");
    /// <summary>仅执行被测入口，不构造预期转换结果。</summary>
    private static LlmConversionResult Convert(JsonObject source, bool nova, bool allowLoss) => LlmProtocolConverter.ConvertRequest(source,
        nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova,
        BedrockConversionFixtures.Options(allowLoss: allowLoss));
}
