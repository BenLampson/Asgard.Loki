using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Express 的最终请求/原始完整及 SSE 结果独立于 provider 输出验证。</summary>
public sealed class ExpressNativeTests
{
    /// <summary>两种显式版本根、完整/流式方法及默认 Key 覆盖都保留原生报文，不把 Key 放进 URL。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, false, true)] [InlineData(false, true, false)] [InlineData(false, true, true)] [InlineData(true, false, false)] [InlineData(true, false, true)] [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task CompleteNativeWireAndResults_MatchGoldens(bool beta, bool stream, bool staleDefault)
    {
        var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(stream ? VertexFixture.Sse() : VertexFixture.Text("express-response")));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/" + (beta ? "v1beta1/" : "v1/") + ExpressFixture.Resource + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent"), request.RequestUri!.AbsoluteUri); Assert.DoesNotContain(ExpressFixture.Key, request.RequestUri.AbsoluteUri); Assert.Equal(ExpressFixture.Key, Assert.Single(request.Headers.GetValues("x-goog-api-key"))); Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("x-goog-user-project")); Assert.Equal(stream ? "text/event-stream" : "application/json", Assert.Single(request.Headers.Accept).MediaType);
            var text = await request.Content!.ReadAsStringAsync(token); Assert.DoesNotContain(ExpressFixture.Key, text); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("express-wire")), JsonNode.Parse(text))); var reply = StreamingTests.Reply(body); if (!stream) reply.Content.Headers.ContentType = new("application/json"); return reply;
        }); var factory = new VertexHttpFactory(handler) { DefaultHeader = staleDefault ? "x-goog-api-key" : null }; var provider = new GoogleVertexExpressLlmProvider(factory); var target = ExpressFixture.Target() with { BaseAddress = new("https://first.invalid/proxy/" + (beta ? "v1beta1/" : "v1/")) };
        await using (var client = ExpressFixture.Client(provider, targets: [target]))
        {
            var input = ExpressFixture.Request(); var original = input.ToJsonString(); var call = new LlmCallOptions { SessionId = "express-session" };
            if (stream)
            {
                var expected = JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("events")); var index = 0;
                await foreach (var item in client.LLM.Gemini.StreamAsync(input.Deserialize<GeminiContentRequest>()!, call, TestContext.Current.CancellationToken))
                {
                    Assert.True(JsonElement.DeepEquals(expected[index], item.Data)); Assert.Equal("wire-vertex", item.EventId); Assert.Equal(1200L, item.RetryMilliseconds); Assert.Equal("vertex.native", item.EventName); Assert.Equal(ExpressFixture.Resource, item.Metadata.UpstreamModel); Assert.Equal(index >= 2, body.Eof); Assert.Equal(index >= 2, body.Disposed); Assert.Equal(index >= 2, Assert.Single(factory.Clients).Disposed); index++;
                }
                Assert.Equal(5, index);
            }
            else
            {
                var result = await client.LLM.Gemini.GenerateAsync(input.Deserialize<GeminiContentRequest>()!, call, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("express-response")), result.Data)); Assert.Equal(ExpressFixture.Resource, result.Metadata.UpstreamModel); Assert.Equal("native-express-version", result.Data.GetProperty("modelVersion").GetString()); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
            }
            Assert.Equal(original, input.ToJsonString());
        }
        Assert.Equal(1, handler.Calls); Assert.Equal("Asgard.Loki.LLM.GoogleVertex.Express", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }
    /// <summary>完整/流式都在工厂回调或 HTTP 等待前拍摄原生请求快照。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InputSnapshot_PrecedesFactoryCreation(bool stream)
    {
        var input = ExpressFixture.Request(); using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("express-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return stream ? StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse()))) : ExpressFixture.Reply(); }); var factory = new VertexHttpFactory(handler) { OnCreate = () => input["contents"] = new JsonArray() }; var provider = new GoogleVertexExpressLlmProvider(factory);
        if (stream) { await foreach (var _ in provider.StreamAsync(input, ExpressFixture.Context(), TestContext.Current.CancellationToken)) { } } else await provider.GenerateAsync(input, ExpressFixture.Context(), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
