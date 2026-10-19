using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>原生流式调用的错误/取消/释放边界与认证失败不触发重放或目标切换。</summary>
public sealed class StreamingTransportTests
{
    /// <summary>可能终态之后的读/释放/取消错误阻止所有暂存终态交付。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("body-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsNativeFinishAndUsage(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse())) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null, OnDispose = failure == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var clock = new VertexClock(); var source = new VertexTokenSource(clock); var observed = new List<GeminiContentEvent>();
        async Task Consume() { await foreach (var item in StreamingTests.Enumerate(new(factory, source, timeProvider: clock), cancel.Token)) observed.Add(item); }
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Assert.ThrowsAsync<IOException>(Consume);
        Assert.Equal(2, observed.Count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>声明长度偏大/偏小，即使包含原生 finish 也不能返回成功尾部。</summary>
    [Theory] [InlineData(-1)] [InlineData(1)]
    public async Task DeclaredLength_MustMatchNormalEof(int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(VertexFixture.Sse()); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var reply = StreamingTests.Reply(stream); reply.Content.Headers.ContentLength = bytes.Length + difference; return Task.FromResult(reply); }); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var observed = new List<GeminiContentEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) observed.Add(item); }); Assert.Equal(2, observed.Count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>HTTP 错误或 200 JSON 根错误保留实际状态和原文，不当成 SSE 或再次认证。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task JsonFailureInsteadOfSse_PreservesOriginalStatus(int status)
    {
        const string error = "{\"error\":{\"code\":403,\"message\":\"原始错误\",\"future\":null}}"; var clock = new VertexClock(); var source = new VertexTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(error, Encoding.UTF8, "application/json") })); var factory = new VertexHttpFactory(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in StreamingTests.Enumerate(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken)) { } }); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>finish 后到达原生错误时保留错误，不能释放此前暂存的成功。</summary>
    [Fact]
    public async Task LateNativeError_DoesNotReleaseHeldTerminal()
    {
        const string error = "{\"error\":{\"message\":\"late error\",\"code\":500}}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Sse() + "data: " + error + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var count = 0;
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in StreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) count++; }); Assert.Equal(error, failure.ResponseBody); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(2, count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>预取消在首次枚举时生效，不访问认证源或 HTTP 工厂。</summary>
    [Fact]
    public async Task PreCancelledEnumeration_DoesNotAcquireResources()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in StreamingTests.Enumerate(new(factory, source, timeProvider: clock), cancel.Token)) { } }); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
