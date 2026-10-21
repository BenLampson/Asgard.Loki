using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Coze.Tests.CozeFixture;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>Coze 原生流的字节/帧/资源/最终取消守卫，不把正常 socket 结束等同于推理完成。</summary>
public sealed class CozeStreamTransportTests
{
    /// <summary>HTTP错误及成功 HTTP 下的原生 JSON code 错误仍保留实际字节，JSON回执不能自动转成轮询。</summary>
    [Theory] [InlineData("binary")] [InlineData("native-json")] [InlineData("ack-json")]
    public async Task HttpErrorOrJsonAcknowledgement_IsNotAStream(string kind)
    {
        byte[] bytes = kind == "binary" ? [0xff, 0, 0x80] : Encoding.UTF8.GetBytes(kind == "native-json" ? "{\"code\":701231,\"msg\":\"native error\",\"future\":null}" : Part("ack").GetRawText());
        using var handler = Reply(new CozeBodyStream(bytes), kind == "binary" ? "application/octet-stream" : "application/json", kind == "binary" ? 429 : 200); var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token);
        var failure = await Record.ExceptionAsync(async () => { await foreach (var _ in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) { } });
        if (kind == "ack-json") Assert.IsType<InvalidDataException>(failure); else { var upstream = Assert.IsType<LlmUpstreamException>(failure); Assert.Equal(bytes, upstream.Body.ToArray()); Assert.Equal(kind == "binary" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, upstream.StatusCode); }
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有 Chat/global终态都保留到正常 EOF 和全部拥有资源关闭，任何晚到故障或取消都不能先返回它们。</summary>
    [Theory] [InlineData("read")] [InlineData("stream-dispose")] [InlineData("client-dispose")] [InlineData("cancel-eof")] [InlineData("cancel-stream")] [InlineData("cancel-client")]
    public async Task LateFailure_HoldsChatTerminalAndDone(string kind)
    {
        using var cancel = new CancellationTokenSource(); var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Text("events.sse")))
        { FailAtEof = kind == "read", FailDispose = kind == "stream-dispose", OnEof = kind == "cancel-eof" ? cancel.Cancel : null, OnDispose = kind == "cancel-stream" ? cancel.Cancel : null };
        using var handler = Reply(body, "text/event-stream"); using var factory = new CozeHttpFactory(handler)
        { OnClientDispose = kind == "client-dispose" ? () => throw new IOException("fixture client") : kind == "cancel-client" ? cancel.Cancel : null };
        var client = new CozeChatClient(factory, Options(), Token); List<CozeChatEvent> events = [];
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in client.StreamAsync(Request(), cancel.Token)) events.Add(item); });
        Assert.NotNull(failure); if (kind.StartsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure); else Assert.IsType<IOException>(failure);
        Assert.Equal(8, events.Count); Assert.DoesNotContain(events, e => e.IsChatTerminal || e.IsTerminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始 SSE 总预算包括字段/注释/分隔符，恰好边界成功，少一个字节便不能发布任何终态。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public async Task ExactRawStreamBudget_IsEnforced(int adjustment)
    {
        var bytes = Encoding.UTF8.GetBytes(Text("events.sse")); using var handler = Reply(new CozeBodyStream(bytes), "text/event-stream"); var client = new CozeChatClient(new CozeHttpFactory(handler), Options() with { MaximumStreamBytes = bytes.Length + adjustment }, Token); List<CozeChatEvent> events = [];
        async Task Invoke() { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) events.Add(item); }
        if (adjustment == 0) { await Invoke(); Assert.True(events[^1].IsTerminal); } else { await Assert.ThrowsAsync<InvalidDataException>(Invoke); Assert.DoesNotContain(events, e => e.IsChatTerminal || e.IsTerminal); }
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>真实 Native done 后的字节/帧尾部不完整或坏编码依然是失败，不能按 native标记提前断开。</summary>
    [Theory] [InlineData("truncated")] [InlineData("utf8")]
    public async Task InvalidTailAfterDone_PreventsTerminal(string kind)
    {
        var wire = Text("events.sse"); byte[] bytes = kind == "utf8" ? [.. Encoding.UTF8.GetBytes(wire + "data: "), 0xff, 10, 10] : Encoding.UTF8.GetBytes(wire + "event: future\ndata: partial");
        using var handler = Reply(new CozeBodyStream(bytes), "text/event-stream"); var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token); var terminal = false;
        var failure = await Record.ExceptionAsync(async () => { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) terminal |= item.IsChatTerminal || item.IsTerminal; });
        Assert.True(failure is InvalidDataException or DecoderFallbackException, failure?.ToString()); Assert.False(terminal);
    }
    /// <summary>长/短/坏/超预算 Content-Length 都阻止已读终态交付。</summary>
    [Theory] [InlineData("short")] [InlineData("long")] [InlineData("bad")] [InlineData("over")]
    public async Task DeclaredLengthMismatch_PreventsTerminal(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(Text("events.sse")); var body = new CozeBodyStream(bytes); using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream");
            if (kind == "bad") response.Content.Headers.TryAddWithoutValidation("Content-Length", "bad"); else response.Content.Headers.ContentLength = kind == "over" ? long.MaxValue : bytes.Length + (kind == "short" ? -1 : 1); return Task.FromResult(response);
        });
        var client = new CozeChatClient(new CozeHttpFactory(handler), Options(), Token); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) terminal |= item.IsChatTerminal || item.IsTerminal; }); Assert.False(terminal); Assert.True(body.Disposed); if (kind is "bad" or "over") Assert.Equal(0, body.ReadCalls);
    }
    /// <summary>完整请求冻结先于流工厂创建，调用者原字典不参与后续 native 数据生成。</summary>
    [Fact]
    public async Task StreamSnapshot_PrecedesFactory()
    {
        var request = Request(); using var handler = new StubHttpHandler(async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(Part("wire_stream"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return new(HttpStatusCode.OK) { Content = new StringContent(Text("events.sse"), Encoding.UTF8, "text/event-stream") }; });
        using var factory = new CozeHttpFactory(handler) { Configure = _ => request.Parameters.Clear() }; var client = new CozeChatClient(factory, Options(), Token);
        await foreach (var _ in client.StreamAsync(request, TestContext.Current.CancellationToken)) { } Assert.Empty(request.Parameters); Assert.Equal(1, handler.Calls);
    }
    /// <summary>EOF 或客户端关闭时实际目标变化不能在先发布完整对话后才被发现。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateActualTargetMutation_PreventsTerminal(bool dispose)
    {
        HttpRequestMessage? sent = null; var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Text("events.sse"))) { OnEof = !dispose ? () => sent!.RequestUri = new("https://other.invalid/chat") : null };
        using var handler = new StubHttpHandler((message, _) => { sent = message; var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response); });
        using var factory = new CozeHttpFactory(handler) { OnClientDispose = dispose ? () => sent!.RequestUri = new("https://other.invalid/chat") : null }; var client = new CozeChatClient(factory, Options(), Token); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in client.StreamAsync(Request(), TestContext.Current.CancellationToken)) terminal |= item.IsChatTerminal || item.IsTerminal; }); Assert.False(terminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>提前退出关闭本次所有拥有资源，工厂保留且不读取剩余对话或执行任何后续查询。</summary>
    [Fact]
    public async Task EarlyExit_LeavesBorrowedFactoryAndUnreadTail()
    {
        var body = new CozeBodyStream(Encoding.UTF8.GetBytes(Text("events.sse"))); using var handler = Reply(body, "text/event-stream"); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token);
        await using (var events = client.StreamAsync(Request(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken)) Assert.True(await events.MoveNextAsync());
        Assert.True(body.Disposed); Assert.False(body.Eof); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>预取消不创建流客户端，实际最后一帧交付后的最终取消也不能变成正常结束。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeOrAfterTerminal_RemainsFailure(bool preCancelled)
    {
        using var cancel = new CancellationTokenSource(); using var handler = Reply(new CozeBodyStream(Encoding.UTF8.GetBytes(Text("events.sse"))), "text/event-stream"); using var factory = new CozeHttpFactory(handler); var client = new CozeChatClient(factory, Options(), Token);
        if (preCancelled) cancel.Cancel();
        if (preCancelled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in client.StreamAsync(Request(), cancel.Token)) { } }); Assert.Empty(factory.Clients); }
        else
        {
            await using var events = client.StreamAsync(Request(), cancel.Token).GetAsyncEnumerator(cancel.Token); for (var i = 0; i < 11; i++) Assert.True(await events.MoveNextAsync()); Assert.True(events.Current.IsTerminal); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync()); Assert.Equal(1, handler.Calls);
        }
    }
}
