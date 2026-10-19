using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>Nova2 high 的输出上限冲突不能借由丢字段或损耗许可获得一个表面有效的请求。</summary>
public sealed class BedrockHighReasoningLimitTests
{
    /// <summary>原生双向映射、两种明确布局和损耗策略都必须检查同一个显式上限。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var fromNova in new[] { false, true }) foreach (var nested in new[] { false, true })
        foreach (var allowLoss in new[] { false, true }) yield return [fromNova, nested, allowLoss];
    }
    /// <summary>完整有效原生金样只改变推理强度，失败不修改正文或下调强度。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public void ExplicitNova2HighWithMaxTokens_FailsWithoutDeletingLimit(bool fromNova, bool nested, bool allowLoss)
    {
        var source = BedrockConversionFixtures.Read("v2", fromNova ? "Nova" : nested ? "ConverseNested" : "ConverseDirect");
        var settings = fromNova ? source["inferenceConfig"]! : nested
            ? source["additionalModelRequestFields"]!["inferenceConfig"]! : source["additionalModelRequestFields"]!;
        settings["reasoningConfig"]!["maxReasoningEffort"] = "high";
        var original = source.ToJsonString(); var options = BedrockConversionFixtures.Options(allowLoss: allowLoss) with
            { BedrockConverseInferenceFormat = nested ? BedrockConverseInferenceFormat.NestedInferenceConfig : BedrockConverseInferenceFormat.Direct };
        var error = Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(source,
            fromNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            fromNova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, options));
        Assert.Contains("maxTokens", error.Message); Assert.Equal(original, source.ToJsonString());
        Assert.Equal(8192, source["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
    }
}
