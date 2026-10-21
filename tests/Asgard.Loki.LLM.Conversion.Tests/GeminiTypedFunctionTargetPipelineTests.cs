using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>typed 函数目标通过公开 SDK 的完整/流式入口核对完整工具历史和最终出站报文。</summary>
public sealed class GeminiTypedFunctionTargetPipelineTests
{
    /// <summary>三种原生源分别保留调用 ID、参数、结果、选择和显式 typed 参数定义。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)]
    [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public async Task CompleteToolHistory_MatchesAuthoredWire(LlmProtocol sourceProtocol, bool stream)
    {
        var fixture = GeminiTypedFunctionTests.Fixture(); var expected = fixture["Gemini"]!.AsObject(); expected.Remove("model");
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
        await using var client = ConvertingProviderTests.Build(handler, sourceProtocol, LlmProtocol.Gemini, options: GeminiTypedFunctionTargetTests.Options());
        var input = fixture[sourceProtocol.ToString()]!.AsObject(); input["model"] = "public"; var before = input.ToJsonString();
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, sourceProtocol, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, sourceProtocol, input)).Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>关闭对象或舍入边界不能在函数定义中悄悄降级，即使普通损耗已获同意。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidDefinition_DoesNotCallUpstream(bool number)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应调用上游。"));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini,
            options: GeminiTypedFunctionTargetTests.Options() with { AllowLoss = true });
        var input = GeminiTypedFunctionTests.Fixture()["ClaudeMessages"]!.AsObject(); input["model"] = "public";
        var schema = GeminiTypedFunctionTests.Parameters(input, LlmProtocol.ClaudeMessages);
        if (number) schema["properties"]!["cap"]!["maximum"] = JsonNode.Parse("9223372036854775807");
        else schema["additionalProperties"] = false;
        await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ClaudeMessages, input));
        Assert.Equal(0, handler.Calls);
    }
}
