using System.Text;
using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Cloudflare.Tests.CloudflareFixture;

namespace Asgard.Loki.LLM.Cloudflare.Tests;

/// <summary>原生 DONE 与真正 EOF/长度/资源边界，错误尾部不能先交付终态。</summary>
public sealed class CloudflareTerminalTests
{
    /// <summary>没有控制帧、重复/晚到/截断/坏编码等窗口都失败，不由 EOF 或业务字段补造终态。</summary>
    [Theory] [InlineData("missing")] [InlineData("duplicate")] [InlineData("late-text")] [InlineData("late-tool")]
    [InlineData("late-metadata")] [InlineData("truncated")] [InlineData("duplicate-json")] [InlineData("unicode")]
    [InlineData("utf8")] [InlineData("fake-done")] [InlineData("non-object")]
    public async Task InvalidWindow_DoesNotPublishTerminal(string kind)
    {
        var wire = Frame(NativeRecord) + Done;
        wire = kind switch
        {
            "missing" => Frame(NativeRecord), "duplicate" => wire + Done, "late-text" => wire + Frame(NativeRecord),
            "late-tool" => wire + Frame("{\"tool_calls\":[{\"name\":\"weather\",\"arguments\":{}}]}"),
            "late-metadata" => wire + Frame("{\"future\":null}"), "truncated" => wire + "data: {",
            "duplicate-json" => wire + Frame("{\"x\":1,\"x\":2}"), "unicode" => wire + Frame("{\"future\":\"\\ud800\"}"),
            "fake-done" => Frame("{\"type\":\"done\",\"done\":true}"), "non-object" => Frame("[]") + Done, _ => wire
        };
        byte[] bytes = kind == "utf8" ? [.. Encoding.UTF8.GetBytes(wire + "data: "), 0xff, 10, 10] : Encoding.UTF8.GetBytes(wire);
        var body = new CloudflareBodyStream(bytes); using var handler = Reply(body, true); using var factory = new CloudflareHttpFactory(handler); var provider = new CloudflareTextProvider(factory); var terminal = false;
        var failure = await Xunit.Record.ExceptionAsync(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; });
        Assert.NotNull(failure); Assert.True(failure is InvalidDataException or JsonException or DecoderFallbackException or InvalidOperationException, failure.ToString());
        Assert.False(terminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>显式原生错误无论位于 DONE 前后，都保留实际状态及完整原生帧错误数据。</summary>
    [Theory] [InlineData(false, "error")] [InlineData(true, "error")] [InlineData(false, "success")] [InlineData(true, "success")]
    [InlineData(false, "errors")] [InlineData(true, "errors")]
    public async Task NativeError_IsNotHiddenAfterDone(bool after, string kind)
    {
        var error = kind switch { "error" => "{\"error\":{\"code\":7,\"message\":\"native error\",\"future\":null}}", "success" => "{\"success\":false,\"result\":null,\"future\":false}", _ => "{\"errors\":[{\"code\":7,\"message\":\"native error\"}],\"future\":null}" };
        using var handler = Reply(new CloudflareBodyStream(Encoding.UTF8.GetBytes(Frame(NativeRecord) + (after ? Done : "") + Frame(error) + (after ? "" : Done))), true);
        var provider = new CloudflareTextProvider(new CloudflareHttpFactory(handler)); var terminal = false;
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; });
        Assert.Equal(System.Net.HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal("text/event-stream", failure.ContentType); Assert.False(terminal); Assert.Equal(1, handler.Calls);
    }
    /// <summary>EOF、源或客户端释放异常/取消都不能先交付保留的实际 DONE。</summary>
    [Theory] [InlineData("read")] [InlineData("stream-dispose")] [InlineData("client-dispose")]
    [InlineData("cancel-eof")] [InlineData("cancel-stream")] [InlineData("cancel-client")]
    public async Task LateFailure_HoldsActualTerminal(string kind)
    {
        using var cancel = new CancellationTokenSource(); var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes(Frame(NativeRecord) + Done))
        { FailAtEof = kind == "read", FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-eof" ? cancel.Cancel : null, OnDispose = kind == "cancel-stream" ? cancel.Cancel : null };
        using var handler = Reply(body, true); using var factory = new CloudflareHttpFactory(handler)
        { OnClientDispose = kind == "client-dispose" ? () => throw new IOException("fixture client") : kind == "cancel-client" ? cancel.Cancel : null };
        var provider = new CloudflareTextProvider(factory); List<WorkersAiTextEvent> events = [];
        var failure = await Xunit.Record.ExceptionAsync(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), cancel.Token)) events.Add(item); });
        Assert.NotNull(failure); if (kind.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.Equal(NativeRecord, Assert.Single(events).Data); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>控制字段与注释可继续到 EOF；纯元数据不变成模型消息，也不替换 DONE 的原始身份。</summary>
    [Theory] [InlineData("\n")] [InlineData("\r\n")] [InlineData("\r")]
    public async Task OriginalControlIdentityAndMetadataTail_AreRetained(string newline)
    {
        var wire = ("id: original\nretry: 0\nevent: native.done\ndata: [DONE]\n\n: final heartbeat\nid: later-control\nretry: 500\n\n").Replace("\n", newline, StringComparison.Ordinal);
        var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes(wire)); using var handler = Reply(body, true); var provider = new CloudflareTextProvider(new CloudflareHttpFactory(handler)); List<WorkersAiTextEvent> events = [];
        await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) events.Add(item);
        var terminal = Assert.Single(events); Assert.True(terminal.IsTerminal); Assert.Equal("original", terminal.EventId); Assert.Equal("native.done", terminal.EventName); Assert.Equal(0, terminal.RetryMilliseconds); Assert.True(body.Eof); Assert.True(body.Disposed);
    }
    /// <summary>提前结束只关闭本次拥有资源，不读取下一帧、不重发也不释放工厂。</summary>
    [Fact]
    public async Task EarlyConsumerExit_ClosesOnlyOwnedResources()
    {
        var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes(Frame(NativeRecord) + Done)); using var handler = Reply(body, true); using var factory = new CloudflareHttpFactory(handler); var provider = new CloudflareTextProvider(factory);
        await using (var events = provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken)) { Assert.True(await events.MoveNextAsync()); Assert.False(events.Current.IsTerminal); }
        Assert.True(body.Disposed); Assert.False(body.Eof); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>消费方得到真正终态后再取消，下一次枚举仍传播取消，不吞掉最终令牌。</summary>
    [Fact]
    public async Task CancellationAfterTerminal_DoesNotReturnNormalEnd()
    {
        using var cancel = new CancellationTokenSource(); using var handler = Reply(new CloudflareBodyStream(Encoding.UTF8.GetBytes(Done)), true); var provider = new CloudflareTextProvider(new CloudflareHttpFactory(handler));
        await using var events = provider.StreamAsync(Payload(), Context(), cancel.Token).GetAsyncEnumerator(cancel.Token); Assert.True(await events.MoveNextAsync()); Assert.True(events.Current.IsTerminal); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync()); Assert.Equal(1, handler.Calls);
    }
}
