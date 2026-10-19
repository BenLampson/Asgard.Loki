using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>共享帧校验后的 Converse 专属错误、终态和传输资源边界。</summary>
public sealed class ConverseStreamTransportTests
{
    /// <summary>即便已读取 messageStop，迟到传输或已知服务错误也不能释放成功。</summary>
    [Theory] [InlineData("exception", false)] [InlineData("exception", true)] [InlineData("error", true)] [InlineData("event", true)]
    public async Task NativeErrors_PreserveOriginalFrameAndStatus(string kind, bool late)
    {
        const string json = "{\"message\":\"原始错误\",\"originalStatusCode\":424,\"future\":null}"; var errorFrame = StreamFixture.Frame(kind, "modelStreamErrorException", json); var wire = new[] { ConverseStreamFixture.Start() }.Concat(late ? [ConverseStreamFixture.Stop(), ConverseStreamFixture.Metadata()] : []).Append(errorFrame).SelectMany(item => item).ToArray(); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var factory = new AwsHttpFactory(handler); var finished = false; var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ConverseStreamFixture.Consume(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, item => finished |= item.EventName == "messageStop")); Assert.False(finished); Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(json, error.ResponseBody); Assert.NotNull(error.GetAwsEventStreamFrame()); Assert.Equal(errorFrame, error.GetAwsEventStreamFrame()!.WireBytes.ToArray()); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>可能成功后的 CRC/截断、迟读、释放或取消故障不交付原生结束帧。</summary>
    [Theory] [InlineData("crc")] [InlineData("truncated")] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsTerminalAndUsageTail(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var wire = ConverseStreamFixture.Bytes(); if (failure is "crc" or "truncated") { var tail = ConverseStreamFixture.Event("future", "{}"); tail[^1] ^= 1; wire = [.. wire, .. (failure == "truncated" ? tail[..^1] : tail)]; } var body = new OllamaBodyStream(wire) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); var count = 0; async Task Consume() => await ConverseStreamFixture.Consume(provider, cancel.Token, _ => count++);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else if (failure is "crc" or "truncated") await Assert.ThrowsAsync<InvalidDataException>(Consume); else await Assert.ThrowsAsync<IOException>(Consume); Assert.Equal(24, count); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始全流字节和 Content-Length 精确检查，不忽略尾帧或自动截断。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task RawTotalAndContentLength_AreExact(string scenario)
    {
        var wire = ConverseStreamFixture.Bytes(); var clock = new AwsClock(); var body = new OllamaBodyStream(wire); using var handler = new StubHttpHandler((_, _) => { var reply = StreamFixture.Reply(body); reply.Content.Headers.ContentLength = wire.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0); return Task.FromResult(reply); }); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1", MaximumBodyBytes = wire.Length - (scenario == "limit" ? 1 : 0) }, timeProvider: clock); var finished = false; async Task Consume() => await ConverseStreamFixture.Consume(provider, TestContext.Current.CancellationToken, item => finished |= item.EventName == "messageStop");
        if (scenario == "exact") { await Consume(); Assert.True(finished); } else { await Assert.ThrowsAsync<InvalidDataException>(Consume); Assert.False(finished); } Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>提前退出/增量后取消不会读取完整来源或产生结束帧。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyExit_ReleasesWithoutCompletion(bool cancelStream)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var body = new OllamaBodyStream(ConverseStreamFixture.Bytes()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); var count = 0;
        if (cancelStream) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConverseStreamFixture.Consume(provider, cancel.Token, _ => { count++; cancel.Cancel(); })); else { await foreach (var _ in provider.ConverseStreamAsync(ConverseFixture.Request(), ConverseFixture.Context(), cancel.Token)) { count++; break; } } Assert.Equal(1, count); Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>普通 JSON/SSE 和未知二进制媒体不模拟成 Converse 成功流。</summary>
    [Theory] [InlineData("json")] [InlineData("sse")] [InlineData("binary-event")]
    public async Task UnsupportedRepresentations_FailExplicitly(string scenario)
    {
        var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(scenario == "binary-event" ? StreamFixture.Reply(StreamFixture.Frame("event", "future_binary", "original bytes", media: "application/octet-stream")) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, scenario == "sse" ? "text/event-stream" : "application/json") })); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); if (scenario == "binary-event") { var error = await Assert.ThrowsAsync<NotSupportedException>(() => ConverseStreamFixture.Consume(provider, TestContext.Current.CancellationToken)); Assert.NotNull(error.GetAwsEventStreamFrame()); Assert.Equal(Encoding.UTF8.GetBytes("original bytes"), error.GetAwsEventStreamFrame()!.Payload.ToArray()); } else await Assert.ThrowsAsync<InvalidDataException>(() => ConverseStreamFixture.Consume(provider, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 错误按原状态和原文本报告，不因二进制操作重试。</summary>
    [Theory] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task HttpError_PreservesOriginalBody(int status)
    {
        const string json = "{\"message\":\"原始 HTTP 错误\",\"future\":null}"; var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") })); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ConverseStreamFixture.Consume(new(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(json, error.ResponseBody); Assert.Null(error.GetAwsEventStreamFrame()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
}
