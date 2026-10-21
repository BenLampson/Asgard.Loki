using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Express 自己的 HTTP 发送路径仍遵守有限字节/长度与 EOF/释放边界。</summary>
public sealed class ExpressTransportTests
{
    /// <summary>HTTP 或根级错误原样保留状态和正文，不尝试 Bearer/另一服务器或重放。</summary>
    [Theory] [InlineData(401, false)] [InlineData(429, true)] [InlineData(503, true)] [InlineData(200, false)] [InlineData(200, true)]
    public async Task OriginalError_IsPreservedWithoutRetry(int status, bool stream)
    {
        const string error = "{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\",\"message\":\"原始错误\",\"future\":null}}"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(error, Encoding.UTF8, "application/json") })); var factory = new VertexHttpFactory(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => ExpressFixture.Direct(new(factory), stream, TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整或流式的迟读/释放/取消故障均不返回成功或释放暂存终态。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "body-dispose")] [InlineData(true, "body-dispose")] [InlineData(false, "client-dispose")] [InlineData(true, "client-dispose")] [InlineData(false, "eof-cancel")] [InlineData(true, "eof-cancel")] [InlineData(false, "client-cancel")] [InlineData(true, "client-cancel")]
    public async Task LateFailure_WithholdsCompletion(bool stream, string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(stream ? VertexFixture.Sse() : VertexFixture.Text("express-response"))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => { var reply = StreamingTests.Reply(body); if (!stream) reply.Content.Headers.ContentType = new("application/json"); return Task.FromResult(reply); }); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new GoogleVertexExpressLlmProvider(factory); var count = 0;
        async Task Consume() { if (stream) { await foreach (var _ in provider.StreamAsync(ExpressFixture.Request(), ExpressFixture.Context(), cancel.Token)) count++; } else await provider.GenerateAsync(ExpressFixture.Request(), ExpressFixture.Context(), cancel.Token); }
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Assert.ThrowsAsync<IOException>(Consume); Assert.Equal(stream ? 2 : 0, count); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整与 SSE 原始字节上限精确计算，超出时不截断结果。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task RawByteLimit_IsExact(bool stream, bool tooSmall)
    {
        var bytes = Encoding.UTF8.GetBytes(stream ? VertexFixture.Sse() : VertexFixture.Text("express-response")); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var reply = StreamingTests.Reply(body); if (!stream) reply.Content.Headers.ContentType = new("application/json"); return Task.FromResult(reply); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexExpressLlmProvider(factory, new() { MaximumBodyBytes = bytes.Length - (tooSmall ? 1 : 0) });
        if (tooSmall) await Assert.ThrowsAsync<InvalidDataException>(() => ExpressFixture.Direct(provider, stream, TestContext.Current.CancellationToken)); else await ExpressFixture.Direct(provider, stream, TestContext.Current.CancellationToken); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>截断的原生候选生命周期不因为更换为 Express 认证而放宽。</summary>
    [Fact]
    public async Task MissingNativeFinish_DoesNotSynthesizeStop()
    {
        var text = "data: {\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n"; var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler); await Assert.ThrowsAsync<InvalidDataException>(() => ExpressFixture.Direct(new(factory), true, TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>预取消完整和流式调用都不创建客户端、不发送请求。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancelled_AcquiresNoResources(bool stream)
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ExpressFixture.Reply())); var factory = new VertexHttpFactory(handler); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExpressFixture.Direct(new(factory), stream, cancel.Token)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
