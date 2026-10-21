using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>独立完整 SSE 金样覆盖工具关联、推理签名、引用、未知字段及用量尾部。</summary>
public sealed class StreamingTests
{
    /// <summary>流式枚举开始后、认证等待前复制请求，不把等待期间修改的内容发送出去。</summary>
    [Fact]
    public async Task NativeStreamSnapshot_PrecedesAuthenticationAwait()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse()))); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var input = VertexFixture.Request();
        await using var iterator = provider.StreamAsync(input, VertexFixture.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var first = iterator.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["contents"] = new JsonArray(); release.SetResult(); Assert.True(await first); while (await iterator.MoveNextAsync()) { }
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整请求与原始事件逐项相等，增量不等待 EOF，而终态尾部通过释放后才交付。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task NativeSse_MatchesFullIndependentGoldens(bool endpoint, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse())); var resource = endpoint ? "projects/project-a/locations/global/endpoints/endpoint-123" : VertexFixture.Resource;
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/v1/" + resource + ":streamGenerateContent?alt=sse", request.RequestUri!.AbsoluteUri); Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("x-goog-api-key"));
            if (quota) Assert.Equal("quota-project", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); else Assert.False(request.Headers.Contains("x-goog-user-project"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return Reply(stream);
        });
        var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, new() { QuotaProjectId = quota ? "quota-project" : null }, timeProvider: clock); await using var client = VertexFixture.Client(provider, targets: [VertexFixture.Target(resource: resource)]);
        var expected = JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("events")); var index = 0; var input = VertexFixture.Request(); var original = input.ToJsonString();
        await foreach (var item in client.LLM.Gemini.StreamAsync(input.Deserialize<GeminiContentRequest>()!, new() { SessionId = "vertex-session" }, TestContext.Current.CancellationToken))
        {
            Assert.True(JsonElement.DeepEquals(expected[index], item.Data)); Assert.Equal("wire-vertex", item.EventId); Assert.Equal(1200L, item.RetryMilliseconds); Assert.Equal("vertex.native", item.EventName); Assert.Equal(resource, item.Metadata.UpstreamModel);
            Assert.Equal(index >= 2, stream.Eof); Assert.Equal(index >= 2, stream.Disposed); Assert.Equal(index >= 2, Assert.Single(factory.Clients).Disposed); index++;
        }
        Assert.Equal(5, index); Assert.Equal(original, input.ToJsonString()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>原生停止、截断、拦截和未来结束原因不归一成成功 STOP，缺省 index 也不写回事件。</summary>
    [Theory] [InlineData("STOP")] [InlineData("MAX_TOKENS")] [InlineData("SAFETY")] [InlineData("FUTURE_REASON")]
    public async Task NativeFinishReason_IsPreserved(string reason)
    {
        var data = "{\"candidates\":[{\"finishReason\":\"" + reason + "\"}]}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<GeminiContentEvent>();
        await foreach (var item in Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { Assert.True(stream.Eof); Assert.True(stream.Disposed); observed.Add(item); }
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(data), Assert.Single(observed).Data)); Assert.False(observed[0].Data.GetProperty("candidates")[0].TryGetProperty("index", out _)); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>提示拦截保留原生对象和用量，等待 EOF，不创造候选或完成事件。</summary>
    [Fact]
    public async Task PromptBlock_RemainsNativeAndWaitsForEof()
    {
        const string data = "{\"promptFeedback\":{\"blockReason\":\"SAFETY\",\"future\":false},\"usageMetadata\":{\"promptTokenCount\":11}}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<GeminiContentEvent>();
        await foreach (var item in Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { Assert.True(stream.Disposed); Assert.True(stream.Eof); observed.Add(item); }
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(data), Assert.Single(observed).Data)); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>令牌轮换/新选择器不改变跨客户端流式调用的完整资源目标。</summary>
    [Fact]
    public async Task StreamingSession_RestoresCompleteTargetWithNewToken()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); var resource = VertexFixture.Resource.Replace("project-a", "project-b");
        using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString()); Assert.Contains("projects/project-b/", request.RequestUri.AbsolutePath); return Task.FromResult(Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse())))); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var targets = new[] { VertexFixture.Target(), VertexFixture.Target("second", resource) };
        for (var index = 0; index < 2; index++)
        {
            await using var client = VertexFixture.Client(provider, store, targets, index == 0 ? 1 : 0); await foreach (var _ in client.LLM.Gemini.StreamAsync(VertexFixture.Request().Deserialize<GeminiContentRequest>()!, new() { SessionId = "vertex-session" }, TestContext.Current.CancellationToken)) { }
        }
        Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>调用者提前退出流会释放本次传输，且不合成完成或重连。</summary>
    [Fact]
    public async Task EarlyConsumerExit_DisposesOnlyOwnedResources()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new VertexHttpFactory(handler);
        await foreach (var _ in Enumerate(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken)) break;
        Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    internal static HttpResponseMessage Reply(Stream stream) { var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; reply.Content.Headers.ContentType = new("text/event-stream"); return reply; }
    internal static IAsyncEnumerable<GeminiContentEvent> Enumerate(GoogleVertexLlmProvider provider, CancellationToken token) => provider.StreamAsync(VertexFixture.Request(), VertexFixture.Context(), token);
}
