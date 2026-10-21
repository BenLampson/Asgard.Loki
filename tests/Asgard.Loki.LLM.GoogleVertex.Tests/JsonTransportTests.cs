using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>正文必须经过正常 EOF、严格 JSON/长度和释放检查，才能返回原生完整结果。</summary>
public sealed class JsonTransportTests
{
    /// <summary>HTTP 与 HTTP 200 根错误保留原始状态和正文，认证只执行一次。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task UpstreamError_PreservesNativeStatusAndBody(int status)
    {
        const string text = "{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\",\"message\":\"原始错误\",\"future\":null}}"; var clock = new VertexClock(); var source = new VertexTokenSource(clock);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") })); var factory = new VertexHttpFactory(handler);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => new GoogleVertexLlmProvider(factory, source, timeProvider: clock).GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(text, error.ResponseBody); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整 JSON 不接受 SSE、数组、重复键、截断和非法字符。</summary>
    [Theory] [InlineData("media")] [InlineData("array")] [InlineData("duplicate")] [InlineData("truncated")] [InlineData("escape")] [InlineData("encoding")]
    public async Task InvalidBody_FailsWithoutRepair(string failure)
    {
        var bytes = failure == "encoding" ? new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 } : Encoding.UTF8.GetBytes(failure switch { "array" => "[]", "duplicate" => "{\"future\":1,\"future\":2}", "truncated" => "{\"future\":", "escape" => "{\"future\":\"\\ud800\"}", _ => VertexFixture.Text("response") });
        var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream, failure == "media" ? "text/event-stream" : "application/json"))); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), timeProvider: clock);
        if (failure is "duplicate" or "truncated") await Assert.ThrowsAnyAsync<JsonException>(() => Call(provider)); else if (failure == "encoding") await Assert.ThrowsAsync<DecoderFallbackException>(() => Call(provider)); else await Assert.ThrowsAsync<InvalidDataException>(() => Call(provider));
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>字节上限精确计算，声明长度偏大/偏小均不返回看似完整的结果。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task ByteAndLengthBoundaries_AreEnforced(string mode)
    {
        var bytes = Encoding.UTF8.GetBytes(VertexFixture.Text("response")); var stream = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var reply = Reply(stream); if (mode is "short" or "long") reply.Content.Headers.ContentLength = bytes.Length + (mode == "short" ? -1 : 1); return Task.FromResult(reply); }); var factory = new VertexHttpFactory(handler); var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), new() { MaximumBodyBytes = bytes.Length - (mode == "limit" ? 1 : 0) }, timeProvider: clock);
        if (mode == "exact") { await Call(provider); Assert.True(stream.Eof); } else await Assert.ThrowsAsync<InvalidDataException>(() => Call(provider)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>读尾、释放或取消故障发生在成功结果对外可见之前。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("body-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_PreventsSuccessfulReturn(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var stream = new OllamaBodyStream(Encoding.UTF8.GetBytes(VertexFixture.Text("response"))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null, OnDispose = failure == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(stream))); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var clock = new VertexClock(); var provider = new GoogleVertexLlmProvider(factory, new VertexTokenSource(clock), timeProvider: clock);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), cancel.Token)); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>安全拦截结果仍是原生数据，不强加候选或伪造成功内容。</summary>
    [Fact]
    public async Task PromptBlock_IsPreservedWithoutInventedCandidate()
    {
        const string text = "{\"promptFeedback\":{\"blockReason\":\"SAFETY\",\"future\":false},\"usageMetadata\":{\"promptTokenCount\":11}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") })); var clock = new VertexClock(); var result = await new GoogleVertexLlmProvider(new VertexHttpFactory(handler), new VertexTokenSource(clock), timeProvider: clock).GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(text), result.Data));
    }
    private static Task<GeminiContent> Call(GoogleVertexLlmProvider provider) => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), TestContext.Current.CancellationToken);
    private static HttpResponseMessage Reply(Stream stream, string media = "application/json") { var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; reply.Content.Headers.ContentType = new(media); return reply; }
}
