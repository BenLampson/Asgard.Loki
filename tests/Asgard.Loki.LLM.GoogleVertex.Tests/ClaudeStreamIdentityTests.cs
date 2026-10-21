using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>新增 Claude 流不改变借用身份/快照/缓存约定，默认策略保留旧配置绑定。</summary>
public sealed class ClaudeStreamIdentityTests
{
    /// <summary>原生请求在首次枚举的认证等待前快照，不能在等待期间修改工具/签名。</summary>
    [Fact]
    public async Task Snapshot_PrecedesAuthenticationAwait()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); };
        using var handler = new StubHttpHandler(async (request, token) => { var expected = JsonNode.Parse(VertexFixture.Text("claude-wire"))!.AsObject(); expected["stream"] = true; Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse()))); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var input = ClaudeFixture.Request();
        await using var iterator = ((IClaudeMessagesAdapter)provider).StreamAsync(input, ClaudeFixture.Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var first = iterator.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); Assert.True(await first); while (await iterator.MoveNextAsync()) { } Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls);
    }
    /// <summary>JSON 后恢复流式会话仍固定原服务器/资源，刷新令牌不重选目标。</summary>
    [Fact]
    public async Task JsonSession_RestoresAsStreamWithSameTargetAndNewToken()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); var resource = ClaudeFixture.Resource.Replace("project-a", "project-b"); using var handler = new StubHttpHandler((request, _) => { Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Contains("projects/project-b/", request.RequestUri.AbsolutePath); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString()); return Task.FromResult(source.Calls == 1 ? ClaudeFixture.Reply() : StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse())))); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var targets = new[] { ClaudeFixture.Target(), ClaudeFixture.Target("second", resource) };
        await using (var first = ClaudeFixture.Client(provider, store, targets, 1)) await ClaudeFixture.Invoke(first);
        await using (var next = ClaudeFixture.Client(provider, store, targets)) { await foreach (var _ in next.LLM.ClaudeMessages.StreamAsync(ClaudeFixture.Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "vertex-claude-session" }, TestContext.Current.CancellationToken)) { } }
        Assert.Equal(2, handler.Calls); Assert.Equal(2, source.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>显式内容块策略参与供应商绑定，旧 Session 不会被悄悄换策略。</summary>
    [Fact]
    public async Task ChangedBlockPolicy_InvalidatesExistingSession()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ClaudeFixture.Reply())); var factory = new VertexHttpFactory(handler);
        await using (var first = ClaudeFixture.Client(new(factory, source, timeProvider: clock), store)) await ClaudeFixture.Invoke(first);
        await using var next = ClaudeFixture.Client(new(factory, source, new() { MaximumTrackedClaudeBlocks = 16 }, timeProvider: clock), store); await Assert.ThrowsAsync<InvalidOperationException>(() => ClaudeFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>省略是明确默认；显式提供零/负数策略在建立供应商时失败。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidBlockPolicy_IsRejected(int maximum)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleVertexLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(new()), new() { MaximumTrackedClaudeBlocks = maximum }));
    }
    /// <summary>预取消流式枚举不能取令牌、创建客户端或发送请求。</summary>
    [Fact]
    public async Task PreCancelledStream_AcquiresNoResources()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ClaudeFixture.Reply())); var factory = new VertexHttpFactory(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, source, timeProvider: clock), cancel.Token)) { } }); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
