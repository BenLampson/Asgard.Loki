using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS 流式错误、原始字节与迟读/释放边界独立于完整 JSON 验证。</summary>
public sealed class MaaSStreamTransportTests
{
    /// <summary>HTTP/JSON/SSE 平台错误保留原始状态及正文，已收到 finish 也不能提前成功。</summary>
    [Theory] [InlineData("http")] [InlineData("json-error")] [InlineData("sse-early")] [InlineData("sse-late")] [InlineData("named-error")]
    public async Task NativeErrors_WithholdSuccessAndPreserveBody(string scenario)
    {
        const string error = "{\"error\":{\"message\":\"原始错误\",\"future\":null}}"; var text = scenario == "named-error" ? "{\"message\":\"named native error\"}" : error; var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(scenario is "http" or "json-error" ? new HttpResponseMessage(scenario == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") } : MaaSStreamFixture.Reply(MaaSStreamFixture.Frame(MaaSStreamFixture.Start) + (scenario is "sse-late" or "named-error" ? MaaSStreamFixture.Frame(MaaSStreamFixture.Finish) : "") + (scenario == "named-error" ? "event: error\n" : "") + MaaSStreamFixture.Frame(text)))); var factory = new VertexHttpFactory(handler); var finish = false;
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => MaaSStreamFixture.Consume(new(factory, source, timeProvider: clock), TestContext.Current.CancellationToken, item => { finish |= item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out _)); })); Assert.False(finish); Assert.Equal(scenario == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, failure.StatusCode); Assert.Equal(text, failure.ResponseBody); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>finish/DONE 之后的迟读、释放故障或取消不能释放任何成功终态。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsTerminalTail(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new VertexClock(); var source = new VertexTokenSource(clock); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(MaaSStreamFixture.Sse())) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamingTests.Reply(body))); var factory = new VertexHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client dispose"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock); var count = 0;
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MaaSStreamFixture.Consume(provider, cancel.Token, _ => count++)); else await Assert.ThrowsAsync<IOException>(() => MaaSStreamFixture.Consume(provider, cancel.Token, _ => count++)); Assert.Equal(3, count); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始字节与长度、UTF-8、重复字段和完整 SSE 事件边界严格检查。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")] [InlineData("utf8")] [InlineData("duplicate")] [InlineData("escape")] [InlineData("truncated-frame")] [InlineData("wrong-media")]
    public async Task InvalidRepresentationOrBounds_CannotComplete(string scenario)
    {
        var text = MaaSStreamFixture.Sse(); if (scenario == "duplicate") text = MaaSStreamFixture.Frame(MaaSStreamFixture.Start) + MaaSStreamFixture.Frame("{\"future\":1,\"future\":2}"); if (scenario == "escape") text = MaaSStreamFixture.Frame(MaaSStreamFixture.Start) + MaaSStreamFixture.Frame("{\"future\":\"\\uD800\"}"); if (scenario == "truncated-frame") text += "data: {\"future\":true}"; if (scenario == "wrong-media") text = "{}"; var bytes = Encoding.UTF8.GetBytes(text); if (scenario == "utf8") bytes = [.. bytes, 0xff]; var body = new OllamaBodyStream(bytes); var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((_, _) => { var reply = StreamingTests.Reply(body); if (scenario == "wrong-media") reply.Content.Headers.ContentType = new("application/json"); reply.Content.Headers.ContentLength = bytes.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0); return Task.FromResult(reply); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source, new() { MaximumBodyBytes = bytes.Length - (scenario == "limit" ? 1 : 0) }, timeProvider: clock); var terminal = false;
        if (scenario == "exact") await MaaSStreamFixture.Consume(provider, TestContext.Current.CancellationToken); else { var error = await Record.ExceptionAsync(() => MaaSStreamFixture.Consume(provider, TestContext.Current.CancellationToken, item => { terminal |= item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String); })); Assert.NotNull(error); Assert.True(error is InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException); Assert.False(terminal); } Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
}
