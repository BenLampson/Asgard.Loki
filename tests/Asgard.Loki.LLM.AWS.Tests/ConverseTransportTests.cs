using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立 Converse 发送入口仍以原始长度、EOF、释放和取消为成功边界。</summary>
public sealed class ConverseTransportTests
{
    /// <summary>HTTP 和平台根级错误保留原始状态/正文，不重试模型或获取凭据。</summary>
    [Theory] [InlineData(403)] [InlineData(429)] [InlineData(503)] [InlineData(200)]
    public async Task OriginalError_PreservesStatusAndBody(int status)
    {
        const string json = "{\"error\":{\"message\":\"原始错误\",\"future\":null}}"; var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") })); var factory = new AwsHttpFactory(handler); var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock).ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(json, failure.ResponseBody); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>Claude 的 type=error 不是 Converse 的已知错误形状，未知原生字段保持原样。</summary>
    [Fact]
    public async Task NativeConverseType_DoesNotInheritClaudeEventMeaning()
    {
        var clock = new AwsClock(); var json = JsonNode.Parse(AwsFixture.Text("converse-response"))!.AsObject(); json["type"] = "error"; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") })); var result = await new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock).ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), TestContext.Current.CancellationToken); Assert.Equal("error", result.Data.GetProperty("type").GetString()); Assert.Equal("tool_use", result.Data.GetProperty("stopReason").GetString());
    }
    /// <summary>完整正文之后的迟读、释放和最终取消不能提前返回成功。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsResult(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(AwsFixture.Text("converse-response"))) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client dispose"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), cancel.Token)); else await Assert.ThrowsAsync<IOException>(() => provider.ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), cancel.Token)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始字节与声明长度严格检查，不能截断或忽略尾部。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task RawBodyBounds_AreExact(string scenario)
    {
        var bytes = Encoding.UTF8.GetBytes(AwsFixture.Text("converse-response")); var body = new OllamaBodyStream(bytes); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentLength = bytes.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0); return Task.FromResult(response); }); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1", MaximumBodyBytes = bytes.Length - (scenario == "limit" ? 1 : 0) }, timeProvider: clock); if (scenario == "exact") await provider.ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidDataException>(() => provider.ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), TestContext.Current.CancellationToken)); Assert.True(body.Disposed);
    }
    /// <summary>重复 JSON、非法转义/UTF-8、数组或二进制媒体都不能修复为成功。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("escape")] [InlineData("utf8")] [InlineData("array")] [InlineData("media")]
    public async Task InvalidRepresentation_IsRejected(string scenario)
    {
        var json = scenario switch { "duplicate" => "{\"future\":1,\"future\":2}", "escape" => "{\"future\":\"\\uD800\"}", "array" => "[]", _ => "{}" }; var bytes = scenario == "utf8" ? new byte[] { 255 } : Encoding.UTF8.GetBytes(json); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OllamaBodyStream(bytes)) }; response.Content.Headers.ContentType = new(scenario == "media" ? "application/vnd.amazon.eventstream" : "application/json"); return Task.FromResult(response); }); var error = await Record.ExceptionAsync(() => new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock).ConverseAsync(ConverseFixture.Request(), ConverseFixture.Context(), TestContext.Current.CancellationToken)); Assert.NotNull(error); Assert.True(error is InvalidDataException or DecoderFallbackException or System.Text.Json.JsonException);
    }
}
