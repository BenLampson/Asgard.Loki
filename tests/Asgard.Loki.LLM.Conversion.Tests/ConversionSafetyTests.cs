using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>验证不支持输入、诊断、原文保留及流式失败边界。</summary>
public sealed class ConversionSafetyTests
{
    /// <summary>未知参数在默认策略下失败，显式允许后返回完整原文和路径。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void UnknownFields_RequireExplicitLossPolicy(string json)
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions); request["future"] = JsonNode.Parse(json);
        var error = Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.Responses));
        Assert.Contains(error.Diagnostics, d => d.Path == "$.future");
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.Responses, new() { AllowLoss = true });
        Assert.Contains(result.Diagnostics, d => d.Path == "$.future"); Assert.True(result.OriginalData.TryGetProperty("future", out _));
        Assert.False(result.Data.TryGetProperty("future", out _));
    }

    /// <summary>同协议完整复制未知字段和签名，不执行跨协议标准化。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions)]
    [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ClaudeMessages)]
    [InlineData(LlmProtocol.Gemini)]
    public void NativeIdentity_PreservesEveryField(LlmProtocol protocol)
    {
        var request = ConversionFixtures.Request(protocol); request["opaque"] = new JsonObject { ["encrypted_content"] = "opaque" };
        var result = LlmProtocolConverter.ConvertRequest(request, protocol, protocol);
        Assert.True(JsonNode.DeepEquals(request, JsonNode.Parse(result.Data.GetRawText()))); Assert.Empty(result.Diagnostics);
        request["opaque"] = null;
        Assert.Equal("opaque", result.Data.GetProperty("opaque").GetProperty("encrypted_content").GetString());
    }

    /// <summary>响应链和持久化语义不能靠允许损耗绕过。</summary>
    [Theory]
    [InlineData("previous_response_id", "\"resp_old\"")]
    [InlineData("store", "false")]
    [InlineData("background", "true")]
    public void StatefulSemantics_AlwaysRejectCrossProtocol(string field, string json)
    {
        var request = ConversionFixtures.Request(LlmProtocol.Responses); request[field] = JsonNode.Parse(json);
        Assert.Throws<LlmConversionException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, LlmProtocol.ChatCompletions, new() { AllowLoss = true }));
    }

    /// <summary>Claude 必须从来源或显式选项获得输出上限。</summary>
    [Fact]
    public void ClaudeMaxTokens_IsNeverInvented()
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions); request.Remove("max_completion_tokens");
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages));
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages, new() { DefaultMaxTokens = 256 });
        Assert.Equal(256, result.Data.GetProperty("max_tokens").GetInt32());
    }

    /// <summary>严格 JSON Schema、effort 和 false 值沿正确的原生路径转换。</summary>
    [Fact]
    public void ChatResponses_MapsSchemaAndReasoningWithoutMutatingSchema()
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions);
        request["response_format"] = JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"result","strict":false,"schema":{"type":"object","properties":{"x":{"type":["null","integer"]}},"additionalProperties":false}}}""");
        request["reasoning_effort"] = "low";
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        var target = JsonNode.Parse(result.Data.GetRawText())!;
        Assert.Equal("json_schema", target["text"]!["format"]!["type"]!.GetValue<string>());
        Assert.False(target["text"]!["format"]!["strict"]!.GetValue<bool>());
        Assert.Equal("low", target["reasoning"]!["effort"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(request["response_format"]!["json_schema"]!["schema"], target["text"]!["format"]!["schema"]));
    }

    /// <summary>未知流式事件不会静默变成成功输出。</summary>
    [Fact]
    public void UnknownStreamEvent_IsDiagnosed()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions);
        var error = Assert.Throws<LlmConversionException>(() => converter.Push(new() { ["type"] = "vendor.future", ["opaque"] = "secret" }));
        Assert.Contains(error.Diagnostics, d => d.Code == "unknown_event");
        Assert.DoesNotContain("secret", error.Message);
    }

    /// <summary>流中途断开时不能补发任何成功终态。</summary>
    [Theory]
    [MemberData(nameof(ProtocolConversionTests.Routes), MemberType = typeof(ProtocolConversionTests))]
    public void TruncatedStream_AlwaysFails(LlmProtocol from, LlmProtocol to)
    {
        var converter = new LlmProtocolStreamConverter(from, to);
        converter.Push(ConversionFixtures.Stream(from).First());
        Assert.Throws<InvalidDataException>(() => converter.Complete());
    }

    /// <summary>源协议错误必须失败，不通过文本包装伪装正常响应。</summary>
    [Fact]
    public void ErrorEvent_DoesNotProduceSuccessfulFinalization()
    {
        var converter = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages, LlmProtocol.Responses);
        Assert.Throws<LlmConversionException>(() => converter.Push(new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "overloaded_error" } }));
    }

    /// <summary>工具结果无法关联时不能猜测或复用别的调用。</summary>
    [Fact]
    public void OrphanToolResult_IsRejected()
    {
        var request = ConversionFixtures.Request(LlmProtocol.ChatCompletions);
        request["messages"]![3]!["tool_call_id"] = "foreign";
        Assert.Throws<ArgumentException>(() => LlmProtocolConverter.ConvertRequest(request, LlmProtocol.ChatCompletions, LlmProtocol.Gemini));
    }
}
