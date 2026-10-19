using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>显式 typed 输出经公开 SDK、真实原生适配器与离线替代 HTTP 处理器验证。</summary>
public sealed class GeminiTypedTargetPipelineTests
{
    /// <summary>三种源的两种调用模式都核对完整最终请求与正确原生操作路径。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)]
    [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public async Task TypedOutput_MatchesAuthoredFinalWire(LlmProtocol sourceProtocol, bool stream)
    {
        var fixture = GeminiTypedTargetTests.Fixture(); var expected = GeminiTypedTargetTests.Expected(fixture, sourceProtocol); expected.Remove("model");
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.True(JsonNode.DeepEquals(expected, body), body!.ToJsonString());
            Assert.EndsWith("models/model-a:" + (stream ? "streamGenerateContent" : "generateContent"), request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ConversionFixtures.Response(LlmProtocol.Gemini).ToJsonString(), Encoding.UTF8, "application/json") };
            var events = string.Concat(ConversionFixtures.Stream(LlmProtocol.Gemini).Select(item => "data: " + item.ToJsonString() + "\n\n"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        await using var client = ConvertingProviderTests.Build(handler, sourceProtocol, LlmProtocol.Gemini, options: GeminiTypedTargetTests.TypedOptions(true));
        var input = fixture[sourceProtocol.ToString()]!.AsObject(); input["model"] = "public"; var before = input.ToJsonString();
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, sourceProtocol, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, sourceProtocol, input)).Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>不支持的关闭对象约束或舍入边界必须在请求上游前失败。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnrepresentableConstraint_DoesNotCallUpstream(bool number)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应调用上游。"));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, options: GeminiTypedTargetTests.TypedOptions(true));
        var input = GeminiTypedTargetTests.Fixture()["ClaudeMessages"]!.AsObject(); input["model"] = "public";
        var schema = input["output_config"]!["format"]!["schema"]!;
        if (number) schema["properties"]!["values"]!["items"]!["maximum"] = JsonNode.Parse("9223372036854775807");
        else schema["additionalProperties"] = false;
        await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ClaudeMessages, input));
        Assert.Equal(0, handler.Calls);
    }
}
