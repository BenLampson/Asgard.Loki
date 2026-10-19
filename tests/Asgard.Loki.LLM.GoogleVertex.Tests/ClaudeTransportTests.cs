using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Claude 原生完整 JSON 的严格传输、资源生命周期与取消边界。</summary>
public sealed class ClaudeTransportTests
{
    /// <summary>HTTP 或原生错误保留状态/正文，不从 Claude 退回 Gemini 或再次获取令牌。</summary>
    [Theory] [InlineData(401)] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task UpstreamFailure_PreservesOriginalBody(int status)
    {
        const string text = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"原始错误\",\"future\":null}}"; var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") })); var factory = new VertexHttpFactory(handler);
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).CreateAsync(ClaudeFixture.Request(), ClaudeFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(text, failure.ResponseBody); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整 Claude 不接收 SSE、重复属性或损坏正文，未知字段也遵守编码边界。</summary>
    [Theory] [InlineData("media")] [InlineData("duplicate")] [InlineData("escape")] [InlineData("truncated")]
    public async Task MalformedCompleteResult_DoesNotProduceSuccess(string mode)
    {
        var text = mode switch { "duplicate" => "{\"future\":1,\"future\":2}", "escape" => "{\"future\":\"\\ud800\"}", "truncated" => "{\"type\":", _ => VertexFixture.Text("claude-response") }; var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(text)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream, mode == "media" ? "text/event-stream" : "application/json"))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), timeProvider: clock);
        if (mode is "duplicate" or "truncated") await Assert.ThrowsAnyAsync<JsonException>(() => Call(provider)); else await Assert.ThrowsAsync<InvalidDataException>(() => Call(provider)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>正文原始字节与声明长度不匹配时不能返回可能截断的工具/签名数据。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task CompleteBodyBounds_AreExact(string mode)
    {
        var bytes = Encoding.UTF8.GetBytes(VertexFixture.Text("claude-response")); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var reply = Reply(stream); if (mode is "short" or "long") reply.Content.Headers.ContentLength = bytes.Length + (mode == "short" ? -1 : 1); return Task.FromResult(reply); }); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumBodyBytes = bytes.Length - (mode == "limit" ? 1 : 0) }, timeProvider: clock);
        if (mode == "exact") { await Call(provider); Assert.True(stream.Eof); } else await Assert.ThrowsAsync<InvalidDataException>(() => Call(provider)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>原生结果在 EOF、释放和最后取消检查全部成功之前不会交付。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("body-cancel")] [InlineData("client-cancel")]
    public async Task LateTransportFailure_WithholdsResult(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Text("claude-response"))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null, OnDispose = failure == "body-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var clock = new VertexClock(); var source = new VertexTokenSource(clock); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateAsync(ClaudeFixture.Request(), ClaudeFixture.Context(), cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => provider.CreateAsync(ClaudeFixture.Request(), ClaudeFixture.Context(), cancel.Token)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    private static Task<ClaudeMessage> Call(GoogleVertexLlmProvider provider) => provider.CreateAsync(ClaudeFixture.Request(), ClaudeFixture.Context(), TestContext.Current.CancellationToken);
    private static HttpResponseMessage Reply(Stream stream, string media = "application/json") { var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; reply.Content.Headers.ContentType = new(media); return reply; }
}
