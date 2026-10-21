using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>原生错误与原始字节/EOF/释放边界，不借 AWS SDK 自动重试掩盖失败。</summary>
public sealed class TransportTests
{
    /// <summary>HTTP 与 Claude 根级错误保留状态和完整原始正文，只发送一次。</summary>
    [Theory] [InlineData(403, false)] [InlineData(429, false)] [InlineData(503, false)] [InlineData(200, false)] [InlineData(200, true)]
    public async Task NativeError_IsPreservedWithoutRetry(int status, bool typeOnly)
    {
        var text = typeOnly ? "{\"type\":\"error\",\"message\":\"原始错误\",\"future\":null}" : "{\"error\":{\"type\":\"access_denied\",\"message\":\"原始错误\",\"future\":null}}"; var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") })); var factory = new AwsHttpFactory(handler); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock).CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(text, failure.ResponseBody); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>完整正文之后的迟读/释放/取消仍然不能交付成功结果。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsResult(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(AwsFixture.Text("response"))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client dispose"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), cancel.Token)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始长度和字节边界精确计算，不截断报文后返回成功。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task BodyBounds_AreExact(string scenario)
    {
        var bytes = Encoding.UTF8.GetBytes(AwsFixture.Text("response")); var clock = new AwsClock(); var body = new OllamaBodyStream(bytes); using var handler = new StubHttpHandler((_, _) => { var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; reply.Content.Headers.ContentLength = bytes.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0); return Task.FromResult(reply); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1", MaximumBodyBytes = bytes.Length - (scenario == "limit" ? 1 : 0) }, timeProvider: clock);
        if (scenario == "exact") await provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidDataException>(() => provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>非法 JSON/编码和错误媒体类型都不能被修复为原生成功。</summary>
    [Theory] [InlineData("array")] [InlineData("duplicate")] [InlineData("escape")] [InlineData("utf8")] [InlineData("media")]
    public async Task InvalidRepresentation_IsRejected(string scenario)
    {
        var text = scenario switch { "array" => "[]", "duplicate" => "{\"future\":1,\"future\":2}", "escape" => "{\"future\":\"\\uD800\"}", _ => "{}" }; var bytes = scenario == "utf8" ? new byte[] { 0xff } : Encoding.UTF8.GetBytes(text); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => { var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OllamaBodyStream(bytes)) }; reply.Content.Headers.ContentType = new(scenario == "media" ? "application/vnd.amazon.eventstream" : "application/json"); return Task.FromResult(reply); }); var factory = new AwsHttpFactory(handler); var failure = await Record.ExceptionAsync(() => new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock).CreateAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.NotNull(failure); Assert.True(failure is InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
