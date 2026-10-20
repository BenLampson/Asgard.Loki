using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Tencent.Tests.HunyuanStreamStateTests;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>原生流式尾部在真实字节、全部资源、来源/期限/取消和实际请求校验之前不可交付。</summary>
public sealed class HunyuanStreamTransportTests
{
    /// <summary>EOF、异步源关闭及客户端关闭发生的任何失败都阻止原生终态交付。</summary>
    [Theory] [InlineData("read")] [InlineData("source-dispose")] [InlineData("client-dispose")]
    [InlineData("eof-cancel")] [InlineData("source-cancel")] [InlineData("client-cancel")]
    [InlineData("eof-expired")] [InlineData("source-expired")] [InlineData("client-expired")]
    [InlineData("eof-identity")] [InlineData("source-identity")] [InlineData("client-identity")]
    [InlineData("eof-wire")] [InlineData("source-wire")] [InlineData("client-wire")]
    public async Task EveryLateFailureWithholdsEntireTerminalTail(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var clock = new HunyuanClock(); var expiry = clock.Now.AddMinutes(1); source.Credentials = new("fixture-tencent-principal", "AKIDFIXTURE", "fixture-secret", expiry); HttpRequestMessage? sent = null;
        void Mutate() { if (kind.EndsWith("cancel", StringComparison.Ordinal)) cancellation.Cancel(); if (kind.EndsWith("expired", StringComparison.Ordinal)) clock.Now = expiry; if (kind.EndsWith("identity", StringComparison.Ordinal)) source.BindingIdentity = "changed-source"; if (kind.EndsWith("wire", StringComparison.Ordinal)) sent!.RequestUri = new("https://changed.example.invalid/"); }
        var stream = new HunyuanReadStream(Encoding.UTF8.GetBytes(HunyuanFixture.NativeSse())) { FailRead = kind == "read", FailDispose = kind == "source-dispose", OnEof = kind.StartsWith("eof-", StringComparison.Ordinal) ? Mutate : null, OnAsyncDispose = kind.StartsWith("source-", StringComparison.Ordinal) ? Mutate : null };
        factory.OnDisposeClient = () => { if (kind == "client-dispose") throw new IOException("fixture client closure failure"); if (kind.StartsWith("client-", StringComparison.Ordinal)) Mutate(); };
        factory.OnSend = (message, _) => { sent = message; var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response); }; List<HunyuanEvent> items = [];
        Assert.NotNull(await Record.ExceptionAsync(async () => { await foreach (var item in new TencentHunyuanProvider(factory, source, timeProvider: clock).StreamAsync(HunyuanFixture.StreamBody(), HunyuanFixture.Context(), cancellation.Token)) items.Add(item); })); Assert.Equal(3, items.Count); Assert.DoesNotContain(items, IsTerminal); Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.Equal(1, source.Calls);
    }
    /// <summary>限额、声明长度、UTF8 与 charset 的错误不能因前面已有原生结束帧而被吞掉。</summary>
    [Theory] [InlineData("bytes")] [InlineData("length-long")] [InlineData("length-short")] [InlineData("length-invalid")] [InlineData("charset")] [InlineData("utf8-tail")] [InlineData("tail-count")] [InlineData("choices")]
    public async Task BoundsAndEncodingRejectWithoutTerminal(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var raw = kind == "choices" ? HunyuanFixture.Frames(HunyuanFixture.Part("indexed_events").EnumerateArray()) : HunyuanFixture.NativeSse(); var bytes = Encoding.UTF8.GetBytes(raw); if (kind == "utf8-tail") bytes = [.. bytes, 255]; var stream = new HunyuanReadStream(bytes);
        factory.OnSend = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.TryAddWithoutValidation("Content-Type", kind == "charset" ? "text/event-stream; charset=utf-16" : "text/event-stream"); if (kind == "length-long") response.Content.Headers.ContentLength = bytes.Length + 1; if (kind == "length-short") response.Content.Headers.ContentLength = bytes.Length - 1; if (kind == "length-invalid") response.Content.Headers.TryAddWithoutValidation("Content-Length", "invalid"); return Task.FromResult(response); };
        var options = new TencentHunyuanOptions { MaximumResponseBytes = kind == "bytes" ? bytes.Length - 1 : bytes.Length + 1, MaximumBufferedTerminalEvents = kind == "tail-count" ? 1 : null, MaximumTrackedChoices = kind == "choices" ? 1 : null }; List<HunyuanEvent> items = [];
        Assert.NotNull(await Record.ExceptionAsync(async () => { await foreach (var item in new TencentHunyuanProvider(factory, source, options, timeProvider: new HunyuanClock()).StreamAsync(HunyuanFixture.StreamBody(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)) items.Add(item); })); Assert.DoesNotContain(items, IsTerminal); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); if (kind == "charset" || kind == "length-invalid") Assert.Equal(0, stream.Reads);
    }
    /// <summary>非 SSE HTTP 错误和 HTTP200 原生错误保留原文，不把完整 JSON 伪装为流。</summary>
    [Theory] [InlineData("http")] [InlineData("native")] [InlineData("json-success")]
    public async Task NonSseResponsesNeverBecomeSyntheticStream(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var bytes = kind == "http" ? new byte[] { 255, 0, 2 } : Encoding.UTF8.GetBytes(HunyuanFixture.Part(kind == "native" ? "native_error" : "response").GetRawText()); var status = kind == "http" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK; var media = kind == "http" ? "application/octet-stream" : "application/json";
        factory.OnSend = (_, _) => { var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentType = new(media); return Task.FromResult(response); };
        var error = await Record.ExceptionAsync(() => HunyuanFixture.ReadAsync(new(factory, source, timeProvider: new HunyuanClock()), TestContext.Current.CancellationToken)); if (kind == "json-success") Assert.IsType<InvalidDataException>(error); else { var upstream = Assert.IsType<LlmUpstreamException>(error); Assert.Equal(status, upstream.StatusCode); Assert.Equal(bytes, upstream.Body.ToArray()); Assert.Equal(media, upstream.ContentType); } Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>调用者提前停止消费会释放读取源和 HTTP 客户端，不会再产生尾帧或重放。</summary>
    [Fact]
    public async Task EarlyConsumerBreakClosesOwnedResources()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var stream = new HunyuanReadStream(Encoding.UTF8.GetBytes(HunyuanFixture.NativeSse())); factory.OnSend = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response); };
        await foreach (var _ in new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).StreamAsync(HunyuanFixture.StreamBody(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)) break;
        Assert.True(stream.Disposed); Assert.False(stream.Eof); Assert.Equal(1, factory.Disposals); Assert.Equal(1, factory.Calls); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>无效原生流请求和预先取消均不能取得凭据或创建 HTTP。</summary>
    [Theory] [InlineData("cancel")] [InlineData("model")] [InlineData("stream")] [InlineData("limit")] [InlineData("unicode")]
    public async Task InvalidStreamInputNeverAcquiresOrSends(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); if (kind == "cancel") cancellation.Cancel(); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var body = HunyuanFixture.StreamBody(); if (kind == "model") body["Model"] = "wrong"; if (kind == "stream") body["Stream"] = false; if (kind == "unicode") body["Future"] = new string((char)0xd800, 1);
        Assert.NotNull(await Record.ExceptionAsync(() => HunyuanFixture.ReadAsync(new(factory, source, new() { MaximumRequestBytes = kind == "limit" ? 1 : 1024 * 1024 }), cancellation.Token, body))); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>尾部/关联上限必须为正整数，构造时完成验证。</summary>
    [Theory] [InlineData(true, 0)] [InlineData(true, -1)] [InlineData(false, 0)] [InlineData(false, -1)]
    public void InvalidStreamPoliciesFailAtConstruction(bool tail, int value)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); Assert.Throws<ArgumentOutOfRangeException>(() => new TencentHunyuanProvider(factory, source, new() { MaximumBufferedTerminalEvents = tail ? value : null, MaximumTrackedChoices = tail ? null : value })); Assert.Equal(0, factory.Creates);
    }
}
