using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS 完整响应必须通过原始字节、终端读取/释放和错误保真验证。</summary>
public sealed class MaaSTransportTests
{
    /// <summary>HTTP 错误和 HTTP 200 根级平台错误保留原始内容，不自动重试。</summary>
    [Theory] [InlineData(401)] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task NativeError_PreservesStatusAndBody(int status)
    {
        const string text = "{\"error\":{\"code\":403,\"message\":\"原始错误\",\"future\":null}}"; var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") })); var factory = new VertexHttpFactory(handler); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock).CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(text, error.ResponseBody); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>迟读、正文/客户端释放及最终取消不能返回成功结果。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsResult(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Text("maas-response"))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => { var response = StreamingTests.Reply(body); response.Content.Headers.ContentType = new("application/json"); return Task.FromResult(response); }); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), cancel.Token)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>精确原始字节上限与声明长度均不得通过截断/忽略尾部来放宽。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task BodyLengthAndLimit_AreExact(string scenario)
    {
        var bytes = Encoding.UTF8.GetBytes(VertexFixture.Text("maas-response")); var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var response = StreamingTests.Reply(body); response.Content.Headers.ContentType = new("application/json"); response.Content.Headers.ContentLength = bytes.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0); return Task.FromResult(response); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source, new() { MaximumBodyBytes = bytes.Length - (scenario == "limit" ? 1 : 0) }, timeProvider: clock);
        if (scenario == "exact") await provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidDataException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整操作不接收 SSE 或数组；原始非法编码、重复键和转义均必须失败。</summary>
    [Theory] [InlineData("sse")] [InlineData("array")] [InlineData("duplicate")] [InlineData("escape")] [InlineData("utf8")]
    public async Task InvalidNativeRepresentation_IsNotRepaired(string scenario)
    {
        var text = scenario switch { "array" => "[]", "duplicate" => "{\"future\":1,\"future\":2}", "escape" => "{\"future\":\"\\uD800\"}", _ => "{}" }; var bytes = scenario == "utf8" ? new byte[] { 0xff } : Encoding.UTF8.GetBytes(text); var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => { var response = StreamingTests.Reply(new OllamaBodyStream(bytes)); response.Content.Headers.ContentType = new(scenario == "sse" ? "text/event-stream" : "application/json"); return Task.FromResult(response); }); var factory = new VertexHttpFactory(handler); var error = await Record.ExceptionAsync(() => new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock).CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.NotNull(error); Assert.True(error is InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
