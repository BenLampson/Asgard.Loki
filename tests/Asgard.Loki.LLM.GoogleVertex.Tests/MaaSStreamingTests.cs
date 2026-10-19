using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS 原生完整 SSE 样本覆盖工具分片、推理、未知字段和用量尾部。</summary>
public sealed class MaaSStreamingTests
{
    /// <summary>全部原生请求/事件保持独立金样，增量先到达而终态在 EOF/释放之后到达。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task NativeStream_MatchesCompleteIndependentGoldens(bool global, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(MaaSStreamFixture.Sse())); var root = "https://first.invalid/proxy/" + (global ? "v1beta1/projects/project-a/locations/global/" : "v1/projects/project-a/locations/us-central1/") + "endpoints/openapi/";
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(root + "chat/completions", request.RequestUri!.AbsoluteUri); Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType); Assert.False(request.Headers.Contains("x-goog-api-key")); if (quota) Assert.Equal("selected-quota", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); else Assert.False(request.Headers.Contains("x-goog-user-project")); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("maas-stream-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamingTests.Reply(body);
        }); var factory = new VertexHttpFactory(handler) { DefaultHeader = "Authorization" }; var provider = new GoogleVertexMaaSLlmProvider(factory, source, new() { QuotaProjectId = quota ? "selected-quota" : null }, timeProvider: clock); await using var client = MaaSFixture.Client(provider, targets: [MaaSFixture.Target() with { BaseAddress = new(root) }]); var input = MaaSFixture.Request(); var original = input.ToJsonString(); var expected = JsonSerializer.Deserialize<JsonElement>(VertexFixture.Text("maas-events")); var index = 0;
        await foreach (var item in client.LLM.ChatCompletions.StreamAsync(input.Deserialize<ChatCompletionRequest>()!, new() { SessionId = "maas-session" }, TestContext.Current.CancellationToken))
        {
            Assert.True(JsonElement.DeepEquals(expected[index], item.Data)); Assert.Equal(index == 5 ? "maas.future" : "maas.chunk", item.EventName); Assert.Equal("maas-wire-id", item.EventId); Assert.Equal(1250L, item.RetryMilliseconds); Assert.Equal(index >= 3, body.Eof); Assert.Equal(index >= 3, body.Disposed); Assert.Equal(index >= 3, Assert.Single(factory.Clients).Disposed); Assert.Equal(MaaSFixture.Model, item.Metadata.UpstreamModel); index++;
        }
        Assert.Equal(6, index); Assert.Equal(original, input.ToJsonString()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Equal("Asgard.Loki.LLM.GoogleVertex.MaaS", Assert.Single(factory.Names)); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>枚举首次取值时在令牌等待之前拍摄流式请求快照。</summary>
    [Fact]
    public async Task StreamSnapshot_PrecedesTokenAwait()
    {
        var clock = new VertexClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var source = new VertexTokenSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); } }; using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VertexFixture.Text("maas-stream-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return MaaSStreamFixture.Reply(MaaSStreamFixture.Sse()); }); var provider = new GoogleVertexMaaSLlmProvider(new VertexHttpFactory(handler), source, timeProvider: clock); var input = MaaSFixture.Request(); await using var iterator = provider.StreamAsync(input, MaaSFixture.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var first = iterator.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); Assert.True(await first); while (await iterator.MoveNextAsync()) { } Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整/流式跨客户端恢复与短期令牌轮换不能改变首次选择的完整上游。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CrossModeSessionRecovery_RetainsWholeTarget(bool firstStream)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); var second = MaaSFixture.Target("second") with { UpstreamModel = "meta/configured-other", BaseAddress = new("https://second.invalid/v1/projects/project-b/locations/global/endpoints/openapi/") }; using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal(second.BaseAddress.AbsoluteUri + "chat/completions", request.RequestUri!.AbsoluteUri); var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; Assert.Equal(second.UpstreamModel, body["model"]!.GetValue<string>()); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString()); return body["stream"]!.GetValue<bool>() ? MaaSStreamFixture.Reply(MaaSStreamFixture.Sse()) : MaaSFixture.Reply(); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock); var targets = new[] { MaaSFixture.Target(), second };
        async Task Invoke(Asgard.Loki.Abstractions.ILokiClient client, bool stream) { if (stream) { await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(MaaSFixture.Request().Deserialize<ChatCompletionRequest>()!, new() { SessionId = "maas-session" }, TestContext.Current.CancellationToken)) { } } else await MaaSFixture.Invoke(client); }
        await using (var first = MaaSFixture.Client(provider, store, targets, 1)) await Invoke(first, firstStream); var before = await store.GetAsync("maas-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30); await using (var next = MaaSFixture.Client(provider, store, targets)) await Invoke(next, !firstStream); var after = await store.GetAsync("maas-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.DoesNotContain("fixture-token", JsonSerializer.Serialize(after)); Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>消费者提前退出释放来源，既不读完也不构造成功终态。</summary>
    [Fact]
    public async Task EarlyExit_ReleasesWithoutCompleting()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(MaaSStreamFixture.Sse())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler); await foreach (var _ in new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock).StreamAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)) break; Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>消费者在增量后取消时释放来源，没有终态或自动重连。</summary>
    [Fact]
    public async Task MidStreamCancellation_WithholdsSuccessAndDisposes()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(MaaSStreamFixture.Sse())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler); var count = 0; await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MaaSStreamFixture.Consume(new(factory, source, timeProvider: clock), cancel.Token, _ => { count++; cancel.Cancel(); })); Assert.Equal(1, count); Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
}
