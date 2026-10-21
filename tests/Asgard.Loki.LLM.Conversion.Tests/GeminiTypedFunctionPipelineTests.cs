using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>typed 函数定义与完整历史经过公开 SDK 后，实际 HTTP 报文仍与独立预期一致。</summary>
public sealed class GeminiTypedFunctionPipelineTests
{
    /// <summary>每个方向的完整和流式调用都核对最终模型、定义、选择和工具历史身份。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)]
    [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public async Task CompleteRequest_ArrivesUnchangedAtWire(LlmProtocol upstream, bool stream)
    {
        var fixture = GeminiTypedFunctionTests.Fixture(); var expected = fixture[upstream.ToString()]!.AsObject();
        expected["model"] = "model-a"; expected["stream"] = stream;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.True(JsonNode.DeepEquals(expected, body), body!.ToJsonString());
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ConversionFixtures.Response(upstream).ToJsonString(), Encoding.UTF8, "application/json") };
            var events = string.Concat(ConversionFixtures.Stream(upstream).Select(item => "data: " + item.ToJsonString() + "\n\n"))
                + (upstream == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, upstream);
        var input = fixture["Gemini"]!.AsObject(); input["model"] = "public"; var before = input.ToJsonString();
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Gemini, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, input)).Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>未知参数约束即使允许损耗，也必须在上游调用前拒绝。</summary>
    [Fact]
    public async Task InvalidDefinition_DoesNotCallUpstream()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应调用上游。"));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses, options: new() { AllowLoss = true });
        var input = GeminiTypedFunctionTests.Source(); input["model"] = "public";
        GeminiTypedFunctionTests.Function(input)["parameters"]!["additionalProperties"] = false;
        await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, input));
        Assert.Equal(0, handler.Calls);
    }
}
