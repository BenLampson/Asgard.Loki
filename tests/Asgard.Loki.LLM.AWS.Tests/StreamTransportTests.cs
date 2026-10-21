using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>二进制错误/迟到失败的原始帧与终态出版边界。</summary>
public sealed class StreamTransportTests
{
    /// <summary>模型化和非模型化错误保留真实 HTTP 状态、外层 JSON/头/帧，不释放之前的成功。</summary>
    [Theory] [InlineData("exception", false)] [InlineData("exception", true)] [InlineData("error", false)] [InlineData("error", true)]
    public async Task OuterErrors_PreserveRawFrameAndWithholdSuccess(string kind, bool late)
    {
        const string payload = "{\"originalMessage\":\"原始错误\",\"originalStatusCode\":424,\"future\":null}"; var errorFrame = StreamFixture.Frame(kind, "modelStreamErrorException", payload); var wire = new[] { StreamFixture.Chunk(StreamFixture.Start) }.Concat(late ? [StreamFixture.Chunk(StreamFixture.Delta), StreamFixture.Chunk(StreamFixture.Stop)] : []).Append(errorFrame).SelectMany(item => item).ToArray(); var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var factory = new AwsHttpFactory(handler); var finished = false; var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => StreamFixture.Consume(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, item => finished |= item.Type is "message_delta" or "message_stop")); Assert.False(finished); Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(payload, error.ResponseBody); var frame = error.GetAwsEventStreamFrame(); Assert.NotNull(frame); Assert.Equal(errorFrame, frame.WireBytes.ToArray()); Assert.Equal(Encoding.UTF8.GetBytes(payload), frame.Payload.ToArray()); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>Claude 原生错误在可能成功之前是失败终态，之后到达则抛错且不发布成功尾部。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeClaudeError_RetainsFailureSemantics(bool late)
    {
        const string json = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"native\",\"future\":null}}"; var wire = new[] { StreamFixture.Chunk(StreamFixture.Start) }.Concat(late ? [StreamFixture.Chunk(StreamFixture.Delta)] : []).Append(StreamFixture.Chunk(json)).SelectMany(item => item).ToArray(); var clock = new AwsClock(); var body = new OllamaBodyStream(wire); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var observed = new List<ClaudeMessageEvent>(); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock);
        if (late) { var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => StreamFixture.Consume(provider, TestContext.Current.CancellationToken, observed.Add)); Assert.Equal(json, failure.ResponseBody); Assert.NotNull(failure.GetAwsEventStreamFrame()); Assert.Single(observed); }
        else { await StreamFixture.Consume(provider, TestContext.Current.CancellationToken, item => { if (item.Type == "error") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.NotNull(item.GetAwsEventStreamFrame()); } observed.Add(item); }); Assert.Equal("error", observed[^1].Type); } Assert.DoesNotContain(observed, item => item.Type is "message_delta" or "message_stop");
    }
    /// <summary>原生 message_stop 之后的 CRC/读取/释放或取消仍不释放成功。</summary>
    [Theory] [InlineData("crc-tail")] [InlineData("truncated-tail")] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")] [InlineData("eof-cancel")] [InlineData("client-cancel")]
    public async Task LateFailure_WithholdsAllPotentialSuccess(string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var wire = StreamFixture.Bytes(); if (failure is "crc-tail" or "truncated-tail") { var tail = StreamFixture.Frame("event", "future", "{}"); tail[^1] ^= 1; wire = [.. wire, .. (failure == "truncated-tail" ? tail[..^1] : tail)]; } var body = new OllamaBodyStream(wire) { FailAtEof = failure == "read", FailDispose = failure == "body-dispose", OnEof = failure == "eof-cancel" ? cancel.Cancel : null }; var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (failure == "client-dispose") throw new IOException("client disposal"); if (failure == "client-cancel") cancel.Cancel(); } }; var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); var count = 0;
        async Task Consume() => await StreamFixture.Consume(provider, cancel.Token, _ => count++);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else if (failure.EndsWith("tail", StringComparison.Ordinal)) await Assert.ThrowsAsync<InvalidDataException>(Consume); else await Assert.ThrowsAsync<IOException>(Consume); Assert.Equal(11, count); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>HTTP 错误不尝试解码为 EventStream，保留原状态/正文且不重试。</summary>
    [Theory] [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task HttpFailure_PreservesStatusAndBody(int status)
    {
        const string json = "{\"message\":\"原始 HTTP 错误\",\"future\":null}"; var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") })); var factory = new AwsHttpFactory(handler); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => StreamFixture.Consume(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(json, error.ResponseBody); Assert.Null(error.GetAwsEventStreamFrame()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>提前退出或在增量后取消会释放资源，不读到 EOF 或虚构原生终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EarlyConsumerExit_ReleasesTransport(bool cancellation)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var body = new OllamaBodyStream(StreamFixture.Bytes()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); var count = 0;
        if (cancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StreamFixture.Consume(provider, cancel.Token, _ => { count++; cancel.Cancel(); })); else { await foreach (var _ in provider.StreamAsync(AwsFixture.Request(), AwsFixture.Context(), cancel.Token)) { count++; break; } } Assert.Equal(1, count); Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
