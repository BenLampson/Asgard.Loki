using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Claude SSE 的 EOF、长度、错误原文、编码及释放失败都不能产生成功终态。</summary>
public sealed class ClaudeStreamTransportTests
{
    /// <summary>Vertex 平台根错误即使没有 Anthropic type，也必须保留原文和实际 HTTP 状态。</summary>
    [Fact]
    public async Task UntypedPlatformError_PreservesRawUpstreamFailure()
    {
        const string error = "{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\",\"message\":\"platform error\"}}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes("data: " + error + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock();
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { } }); Assert.Equal(error, failure.ResponseBody); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已读取原生 finish 仍须等正常 EOF、释放和取消检查，不能提前向调用方发布。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("body-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsAllPossibleTerminals(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse())) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null, OnDispose = failure == "body-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var clock = new VertexClock(); var source = new VertexTokenSource(clock); var count = 0;
        async Task Consume() { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, source, timeProvider: clock), cancel.Token)) count++; }
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Assert.ThrowsAsync<IOException>(Consume); Assert.Equal(11, count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>Content-Length 偏大/偏小均不能释放暂存的完成消息。</summary>
    [Theory] [InlineData(-1)] [InlineData(1)]
    public async Task DeclaredLength_MustMatchActualEof(int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(ClaudeFixture.Sse()); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = StreamingTests.Reply(stream); response.Content.Headers.ContentLength = bytes.Length + difference; return Task.FromResult(response); }); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var count = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) count++; }); Assert.Equal(11, count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>HTTP 错误和 200 JSON 错误正文不降级为另一种协议或自动重新认证。</summary>
    [Theory] [InlineData(401)] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task JsonFailureInsteadOfSse_PreservesOriginalError(int status)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"原生错误\"}}"; var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(error, Encoding.UTF8, "application/json") })); var factory = new VertexHttpFactory(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken)) { } }); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>可能成功之后的原生错误保留原文，之前暂存的 message_delta/stop 均不能交付。</summary>
    [Fact]
    public async Task ErrorAfterPossibleSuccess_DoesNotPublishHeldFinish()
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"late error\"}}"; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse() + "event: error\ndata: " + error + "\n\n")); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var count = 0;
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) count++; }); Assert.Equal(error, failure.ResponseBody); Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(11, count); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>未知 JSON 字段中的编码/转义/重复属性同样严格检查，不接收 Chat DONE。</summary>
    [Theory] [InlineData("encoding")] [InlineData("escape")] [InlineData("duplicate")] [InlineData("done")]
    public async Task MalformedSseData_IsNotRepaired(string failure)
    {
        var bytes = failure == "encoding" ? Encoding.UTF8.GetBytes("data: {\"future\":\"").Concat(new byte[] { 255 }).Concat(Encoding.UTF8.GetBytes("\"}\n\n")).ToArray() : Encoding.UTF8.GetBytes(failure switch { "escape" => "data: {\"type\":\"ping\",\"future\":\"\\ud800\"}\n\n", "duplicate" => "data: {\"type\":\"ping\",\"future\":1,\"future\":2}\n\n", _ => ClaudeFixture.Sse() + "data: [DONE]\n\n" });
        var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock();
        async Task Consume() { await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, new VertexTokenSource(clock), timeProvider: clock), TestContext.Current.CancellationToken)) { } }
        if (failure == "encoding") await Assert.ThrowsAsync<DecoderFallbackException>(Consume); else if (failure == "escape") await Assert.ThrowsAsync<InvalidDataException>(Consume); else await Assert.ThrowsAnyAsync<JsonException>(Consume); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>提前退出枚举立即释放本次传输，借用对象不释放也不重连。</summary>
    [Fact]
    public async Task EarlyConsumerExit_DisposesOwnedResourcesOnly()
    {
        var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(ClaudeFixture.Sse())); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(stream))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var source = new VertexTokenSource(clock);
        await foreach (var _ in ClaudeStreamingTests.Enumerate(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken)) break; Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.False(source.Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
}
