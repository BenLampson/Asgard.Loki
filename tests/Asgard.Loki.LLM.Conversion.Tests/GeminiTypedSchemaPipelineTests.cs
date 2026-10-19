using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>typed 输出 Schema 通过公开 SDK 的三个目标和两种传输模式验证最终报文。</summary>
public sealed class GeminiTypedSchemaPipelineTests
{
    /// <summary>目标协议收到独立快照中的约束，推理配置共存且只执行一次 HTTP 请求。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)]
    [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public async Task TypedSchema_ArrivesAtFinalWire(LlmProtocol upstream, bool stream)
    {
        var fixture = GeminiTypedSchemaTests.Fixture(); var input = fixture["Gemini"]!.AsObject(); input["model"] = "public";
        input["generationConfig"]!["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "MEDIUM" };
        var before = input.ToJsonString(); var expected = Schema(fixture[upstream.ToString()]!.AsObject(), upstream);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal("model-a", body["model"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(expected, Schema(body, upstream)));
            Assert.Equal("medium", Effort(body, upstream).ToLowerInvariant());
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ConversionFixtures.Response(upstream).ToJsonString(), Encoding.UTF8, "application/json") };
            var events = string.Concat(ConversionFixtures.Stream(upstream).Select(item => "data: " + item.ToJsonString() + "\n\n"))
                + (upstream == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, upstream, options: new() { DefaultSchemaName = "result" });
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Gemini, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, input)).Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>不能表达的校验约束必须在调用上游之前失败，不能借允许损耗发出放宽请求。</summary>
    [Fact]
    public async Task UnknownConstraint_DoesNotCallUpstream()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应调用上游。"));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.Responses,
            options: new() { DefaultSchemaName = "result", AllowLoss = true });
        var input = GeminiTypedSchemaTests.Fixture()["Gemini"]!.AsObject(); input["model"] = "public";
        input["generationConfig"]!["responseSchema"]!["additionalProperties"] = false;
        await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Gemini, input));
        Assert.Equal(0, handler.Calls);
    }
}
