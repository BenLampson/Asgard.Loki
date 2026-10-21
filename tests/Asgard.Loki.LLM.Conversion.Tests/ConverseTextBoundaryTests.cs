using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.ConverseTextFixtures;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>完整有效金样的单项变体验证字段损耗、工具身份与原生终态，不以缺字段掩盖目标边界。</summary>
public sealed class ConverseTextBoundaryTests
{
    /// <summary>超出目标协议范围的采样值或停止串不缩放、裁剪，也不以损耗许可绕过。</summary>
    [Theory] [InlineData("temperature", "2")] [InlineData("temperature", "-0.1")]
    [InlineData("top_p", "1.1")] [InlineData("top_p", "null")] [InlineData("max_tokens", "0")]
    [InlineData("max_tokens", "1.5")] [InlineData("stop", "[\"\"]")]
    public void InvalidTargetSettings_FailWithoutRewriting(string key, string value)
    {
        var source = Read(LlmProtocol.ChatCompletions, "request"); source[key] = JsonNode.Parse(value); var original = source.ToJsonString();
        Assert.NotNull(Record.Exception(() => Convert(source, false, true))); Assert.Equal(original, source.ToJsonString());
    }

    /// <summary>显式损耗许可只作用于已报告的普通扩展，不能取消执行约束。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnknownFields_AreDiagnosedWithOriginalSnapshots(bool response)
    {
        var source = Read(LlmProtocol.ChatCompletions, response ? "nativeResponse" : "request"); source["future_extension"] = null;
        var failure = Assert.Throws<LlmConversionException>(() => Convert(source, response, false));
        Assert.Equal("$.future_extension", Assert.Single(failure.Diagnostics).Path);
        var result = Convert(source, response, true); Assert.Equal("$.future_extension", Assert.Single(result.Diagnostics).Path);
        Assert.True(result.OriginalData.TryGetProperty("future_extension", out _)); Assert.False(result.Data.TryGetProperty("future_extension", out _));
    }

    /// <summary>推理方言、并行承诺、禁用工具和托管工具没有通用 Converse 等价表示。</summary>
    [Theory] [InlineData("reasoning")] [InlineData("parallel")] [InlineData("none")] [InlineData("hosted")]
    [InlineData("stream")] [InlineData("continuation")] [InlineData("store")] [InlineData("background")]
    public void ExecutionControls_AreNotDroppedUnderLossConsent(string kind)
    {
        var source = Read(LlmProtocol.ChatCompletions, "request");
        if (kind == "reasoning") source["reasoning_effort"] = "high";
        if (kind == "parallel") source["parallel_tool_calls"] = false;
        if (kind == "none") source["tool_choice"] = "none";
        if (kind == "hosted") source["tools"]!.AsArray().Add(new JsonObject { ["type"] = "web_search" });
        if (kind == "stream") source["stream"] = true;
        if (kind == "continuation") source["previous_response_id"] = "opaque";
        if (kind == "store") source["store"] = false;
        if (kind == "background") source["background"] = false;
        var failure = Record.Exception(() => Convert(source, false, true)); Assert.NotNull(failure);
        if (kind == "stream") Assert.IsType<NotSupportedException>(failure); else Assert.IsType<LlmConversionException>(failure);
    }

    /// <summary>调用 ID 不按函数名称重写，也不能重用结果、接受孤儿或重复调用。</summary>
    [Theory] [InlineData("duplicate-call")] [InlineData("orphan")] [InlineData("duplicate-result")]
    [InlineData("call-role")] [InlineData("result-role")] [InlineData("invalid-arguments")] [InlineData("duplicate-argument-key")]
    public void InvalidToolAssociations_AreRejected(string kind)
    {
        var source = Read(LlmProtocol.ChatCompletions, "request"); var messages = source["messages"]!.AsArray();
        var assistant = messages.Single(node => node!["tool_calls"] is not null)!;
        var results = messages.Where(node => node!["role"]!.GetValue<string>() == "tool").ToArray();
        if (kind == "duplicate-call") assistant["tool_calls"]![1]!["id"] = "history-a";
        if (kind == "orphan") results[0]!["tool_call_id"] = "foreign";
        if (kind == "duplicate-result") results[1]!["tool_call_id"] = results[0]!["tool_call_id"]!.DeepClone();
        if (kind == "call-role") assistant["role"] = "user";
        if (kind == "result-role") results[0]!["role"] = "assistant";
        if (kind == "invalid-arguments") assistant["tool_calls"]![0]!["function"]!["arguments"] = "[]";
        if (kind == "duplicate-argument-key") assistant["tool_calls"]![0]!["function"]!["arguments"] = "{\"n\":1,\"n\":2}";
        Assert.NotNull(Record.Exception(() => Convert(source, false, true)));
    }

    /// <summary>完整响应的原生计数、消息角色、联合分支和终止原因不能凭缺省或损耗许可修补。</summary>
    [Theory] [InlineData("missing-total")] [InlineData("null-input")] [InlineData("negative-output")]
    [InlineData("missing-metrics")] [InlineData("cache")] [InlineData("stop")] [InlineData("role")]
    [InlineData("union")] [InlineData("reasoning")] [InlineData("duplicate-call")] [InlineData("finish-mismatch")]
    public void InvalidNativeReplies_DoNotBecomeSuccess(string kind)
    {
        var source = Read(LlmProtocol.ChatCompletions, "nativeResponse"); var content = source["output"]!["message"]!["content"]!.AsArray();
        if (kind == "missing-total") source["usage"]!.AsObject().Remove("totalTokens");
        if (kind == "null-input") source["usage"]!["inputTokens"] = null;
        if (kind == "negative-output") source["usage"]!["outputTokens"] = -1;
        if (kind == "missing-metrics") source.Remove("metrics");
        if (kind == "cache") source["usage"]!["cacheReadInputTokens"] = 1;
        if (kind == "stop") source["stopReason"] = "future_stop";
        if (kind == "role") source["output"]!["message"]!["role"] = "user";
        if (kind == "union") content[0]!["toolUse"] = new JsonObject();
        if (kind == "reasoning") content.Add(new JsonObject { ["reasoningContent"] = new JsonObject { ["signature"] = "opaque" } });
        if (kind == "duplicate-call") content[2]!["toolUse"]!["toolUseId"] = "next-a";
        if (kind == "finish-mismatch") { content.RemoveAt(2); content.RemoveAt(1); }
        Assert.NotNull(Record.Exception(() => Convert(source, true, true)));
    }

    /// <summary>历史已消费的 ID 仍属于原始调用域，返回新工具不能复用它。</summary>
    [Theory] [MemberData(nameof(ConverseTextCompleteTests.Protocols), MemberType = typeof(ConverseTextCompleteTests))]
    public void ReplyCannotReuseHistoricalId(LlmProtocol protocol)
    {
        var request = LlmProtocolConverter.ConvertRequest(Read(protocol, "request"), protocol, LlmProtocol.BedrockConverse, new() { AllowLoss = true });
        var source = Read(protocol, "nativeResponse"); source["output"]!["message"]!["content"]![1]!["toolUse"]!["toolUseId"] = "history-a";
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertResponse(source, request.Context!));
        Assert.Equal("tool_call_id_reused", Assert.Single(error.Diagnostics).Code);
    }

    /// <summary>任何混合文本流都在创建状态机时拒绝，不因完整转换能力新增而被误导到其他目标。</summary>
    [Theory] [MemberData(nameof(ConverseTextCompleteTests.Protocols), MemberType = typeof(ConverseTextCompleteTests))]
    public void MixedStreamsRemainExplicitlyUnsupported(LlmProtocol protocol)
    {
        Assert.Throws<NotSupportedException>(() => new LlmProtocolStreamConverter(protocol, LlmProtocol.BedrockConverse));
        Assert.Throws<NotSupportedException>(() => new LlmProtocolStreamConverter(LlmProtocol.BedrockConverse, protocol));
    }

    /// <summary>只调用本轮有完整金样证明的方向，不把原生指标或目标默认值补入反向能力。</summary>
    private static LlmConversionResult Convert(JsonObject source, bool response, bool allowLoss) => response
        ? LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockConverse, LlmProtocol.ChatCompletions, new() { AllowLoss = allowLoss })
        : LlmProtocolConverter.ConvertRequest(source, LlmProtocol.ChatCompletions, LlmProtocol.BedrockConverse, new() { AllowLoss = allowLoss });
}
