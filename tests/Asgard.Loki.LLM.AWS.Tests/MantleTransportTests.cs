using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Mantle Messages 原始 HTTP/JSON/SSE 的资源、原生错误和终态边界。</summary>
public sealed class MantleTransportTests
{
    /// <summary>完整/流式方法分别经历正常原生终态后的读取、释放与最终取消故障。</summary>
    public static IEnumerable<object[]> Faults()
    {
        foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "read", "body", "client", "eof-cancel", "body-cancel", "client-cancel" }) yield return [stream, fault];
    }
    /// <summary>任何晚期故障都不能公开已暂存的结束原因或原生 stop。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task LateFaults_DoNotReleaseSuccess(bool stream, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock);
        var body = new OllamaBodyStream(Bytes(stream ? Finished : "{\"id\":\"msg\"}")) { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancel.Cancel : null, OnDispose = fault == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client"); if (fault == "client-cancel") cancel.Cancel(); } };
        var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); List<ClaudeMessageEvent> seen = [];
        var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, cancel.Token, seen); else await provider.CreateAsync(Request(), Context(), cancel.Token); });
        if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>只有异步释放失败或取消时也必须丢弃成功终态。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task AsyncOnlyDisposal_FailsBeforeSuccess(bool stream, bool cancelled)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock();
        var body = new MantleAsyncDisposeStream(Bytes(stream ? Finished : "{\"id\":\"msg\"}"), () => { if (cancelled) cancel.Cancel(); }, !cancelled);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, stream))); var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); List<ClaudeMessageEvent> seen = [];
        var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, cancel.Token, seen); else await provider.CreateAsync(Request(), Context(), cancel.Token); });
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error); Assert.True(body.AsyncDisposed); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>终态/尾部在 EOF 和全部释放后才交付，首个开始帧仍保持即时流式。</summary>
    [Fact]
    public async Task IncrementalStart_AndDelayedTerminal_AreBothPreserved()
    {
        var clock = new AwsClock(); var body = new OllamaBodyStream(Bytes(Finished + Frame("{\"type\":\"vendor.future\",\"error\":{\"business\":true}}"))); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AwsHttpFactory(handler); List<ClaudeMessageEvent> seen = [];
        await Drain(new(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock), TestContext.Current.CancellationToken, seen, item =>
        {
            if (item.Type == "message_start") { Assert.False(body.Eof); Assert.False(body.Disposed); }
            else { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
        }); Assert.Equal(4, seen.Count); Assert.Equal("vendor.future", seen[^1].Type);
    }
    /// <summary>HTTP/原生完整错误保留原始状态正文，不重试或回退到 Runtime。</summary>
    [Theory] [InlineData(false, 429)] [InlineData(true, 503)] [InlineData(false, 200)]
    public async Task Errors_PreserveOriginalStatusAndBody(bool stream, int status)
    {
        const string body = "{\"type\":\"error\",\"error\":{\"type\":\"native_error\",\"future\":false}}"; var clock = new AwsClock(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(body)), false, status: (HttpStatusCode)status))); var source = new AwsCredentialSource(clock); var provider = new AwsMantleLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock);
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, []); else await provider.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); }); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Null(error.GetAwsEventStreamFrame()); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>迟到原生错误撤回成功候选；首个原生错误仍作为原生失败事件交付。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeErrors_RemainFailures(bool late)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"future\":false}}"; var clock = new AwsClock(); var wire = (late ? Finished : "") + Frame(error); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(wire)), true))); var provider = new AwsMantleLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock); List<ClaudeMessageEvent> seen = [];
        if (late) { var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => Drain(provider, TestContext.Current.CancellationToken, seen)); Assert.Equal(error, failure.ResponseBody); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); }
        else { await Drain(provider, TestContext.Current.CancellationToken, seen); Assert.Equal(error, Assert.Single(seen).Data.GetRawText()); }
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>源正文上限和实际声明长度覆盖 JSON/SSE；超限不能截断成成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RawBounds_AreExact(bool stream)
    {
        var bytes = Bytes(stream ? Finished : "{\"id\":\"msg\",\"future\":\"中文\"}"); var clock = new AwsClock();
        foreach (var delta in new[] { -1, 0, 1 })
        {
            using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(bytes), stream, bytes.Length + delta))); var provider = new AwsMantleLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1", MaximumBodyBytes = bytes.Length }, timeProvider: clock); List<ClaudeMessageEvent> seen = [];
            var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, seen); else await provider.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); });
            if (delta == 0) Assert.Null(error); else { Assert.IsType<InvalidDataException>(error); Assert.DoesNotContain(seen, item => item.Type is "message_delta" or "message_stop"); }
        }
        using var over = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(bytes), stream))); var limited = new AwsMantleLlmProvider(new AwsHttpFactory(over), new AwsCredentialSource(clock), new() { Region = "us-east-1", MaximumBodyBytes = bytes.Length - 1 }, timeProvider: clock);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await Drain(limited, TestContext.Current.CancellationToken, []); else await limited.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); });
    }
    /// <summary>严格 JSON/UTF-8 不跳过未知字段内部的重复或无效转义。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidEncodingOrJson_IsRejected(bool stream)
    {
        var clock = new AwsClock(); foreach (var json in new[] { "{\"type\":\"message_stop\",\"type\":\"message_start\"}", "{\"future\":{\"n\":1,\"n\":2}}", "{\"future\":\"\\uD800\"}", "[]" })
        {
            using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(Bytes(stream ? Frame(json) : json)), stream))); var provider = new AwsMantleLlmProvider(new AwsHttpFactory(handler), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock);
            var error = await Record.ExceptionAsync(async () => { if (stream) await Drain(provider, TestContext.Current.CancellationToken, []); else await provider.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); }); Assert.True(error is InvalidDataException or JsonException, error?.ToString());
        }
        byte[] invalid = [.. Bytes(stream ? "data: {\"x\":\"" : "{\"x\":\""), 0xFF, .. Bytes(stream ? "\"}\n\n" : "\"}")]; using var bad = new StubHttpHandler((_, _) => Task.FromResult(Reply(new MemoryStream(invalid), stream))); var p = new AwsMantleLlmProvider(new AwsHttpFactory(bad), new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock);
        await Assert.ThrowsAsync<DecoderFallbackException>(async () => { if (stream) await Drain(p, TestContext.Current.CancellationToken, []); else await p.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); });
    }
    /// <summary>读者提前退出释放本次 HTTP，既不读到 EOF 也不合成成功。</summary>
    [Fact]
    public async Task EarlyExit_ReleasesWithoutCompletion()
    {
        var clock = new AwsClock(); var body = new OllamaBodyStream(Bytes(Finished)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, true))); var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock);
        await foreach (var item in provider.StreamAsync(Request(), Context(), TestContext.Current.CancellationToken)) { Assert.Equal("message_start", item.Type); Assert.False(body.Eof); break; }
        Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
