using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.ConverseTextFixtures;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>四种文本请求经同一私有中间表示调用原生 Converse，不把不同工具结果种类混淆。</summary>
public sealed class ConverseTextCompleteTests
{
    /// <summary>每个文本公开入口均有完整独立原生目标和反向结果样本。</summary>
    public static TheoryData<LlmProtocol> Protocols => [LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
    /// <summary>完整请求、工具反序结果、严格 Schema 和数值不丢字段，也不修改源对象。</summary>
    [Theory] [MemberData(nameof(Protocols))]
    public void CompleteRequestAndReply_MatchIndependentGoldens(LlmProtocol protocol)
    {
        var source = Read(protocol, "request"); var before = source.ToJsonString();
        var request = LlmProtocolConverter.ConvertRequest(source, protocol, LlmProtocol.BedrockConverse);
        Equal(Read(protocol, "nativeRequest"), request.Data); Equal(source, request.OriginalData); Assert.Empty(request.Diagnostics); Assert.Equal(before, source.ToJsonString());
        var output = Read(protocol, "nativeResponse"); var response = LlmProtocolConverter.ConvertResponse(output, request.Context!);
        Assert.True(JsonNode.DeepEquals(Read(protocol, "response"), NormalizeResponse(response.Data, protocol)), response.Data.GetRawText());
        Equal(output, response.OriginalData); Assert.Same(request.Context, response.Context); Assert.Empty(response.Diagnostics);
        var nativeSchema = request.Data.GetProperty("outputConfig").GetProperty("textFormat").GetProperty("structure").GetProperty("jsonSchema");
        Assert.Equal(protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses, nativeSchema.TryGetProperty("name", out _));
        using var schema = JsonDocument.Parse(nativeSchema.GetProperty("schema").GetString()!);
        Assert.Equal("9007199254740993", schema.RootElement.GetProperty("properties").GetProperty("n").GetProperty("minimum").GetRawText());
        var result = request.Data.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("toolResult");
        Assert.Equal("history-b", result.GetProperty("toolUseId").GetString());
        Assert.True(result.GetProperty("content")[0].TryGetProperty(protocol == LlmProtocol.Gemini ? "json" : "text", out _));
    }

    /// <summary>原生结果不提供模型字段，独立响应投影保持未知；只有明确上下文可提供请求标签。</summary>
    [Theory] [MemberData(nameof(Protocols))]
    public void ResponseWithoutContext_DoesNotInventNativeModel(LlmProtocol protocol)
    {
        var source = Read(protocol, "nativeResponse"); var result = LlmProtocolConverter.ConvertResponse(source, LlmProtocol.BedrockConverse, protocol);
        var expected = Read(protocol, "response"); expected[protocol == LlmProtocol.Gemini ? "modelVersion" : "model"] = "";
        Assert.True(JsonNode.DeepEquals(expected, NormalizeResponse(result.Data, protocol))); Equal(source, result.OriginalData);
    }

    /// <summary>请求上下文与已交付原文都不依赖调用者之后修改模型或历史集合。</summary>
    [Theory] [MemberData(nameof(Protocols))]
    public void RequestContextAndResult_AreIndependentSnapshots(LlmProtocol protocol)
    {
        var input = Read(protocol, "request"); var request = LlmProtocolConverter.ConvertRequest(input, protocol, LlmProtocol.BedrockConverse);
        input["model"] = "changed"; input.Clear(); var native = Read(protocol, "nativeResponse");
        var response = LlmProtocolConverter.ConvertResponse(native, request.Context!); native.Clear();
        Assert.Equal("public", response.Data.GetProperty(protocol == LlmProtocol.Gemini ? "modelVersion" : "model").GetString());
        Assert.Equal("public", request.OriginalData.GetProperty("model").GetString()); Assert.True(response.OriginalData.TryGetProperty("output", out _));
    }
}
