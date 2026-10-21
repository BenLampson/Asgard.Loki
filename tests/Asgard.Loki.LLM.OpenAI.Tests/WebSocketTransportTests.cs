using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.OpenAI.Internal;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生字节传输的分帧、大小、错误、并发与资源所有权测试。</summary>
public sealed class WebSocketTransportTests
{
    /// <summary>单字节分片仍完整保留中文、原生事件和未知字段；消息之间不拼接。</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(8192)]
    public async Task TextFragments_PreserveCompleteIndependentJson(int size)
    {
        var socket = new StubWebSocket(); await using var transport = new ResponsesWebSocketTransport(socket, 16384);
        const string first = """{"type":"vendor.future","stream_id":"lane","text":"你好🙂","opaque":{"zero":0,"flag":false,"nil":null}}""";
        socket.Text(first, size); socket.Text("""{"type":"response.created","response":{"id":"resp_1"}}""", size); socket.PeerClose();
        var value = await transport.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first, value!.Value.GetRawText()); Assert.Equal("response.created", (await transport.ReadAsync(TestContext.Current.CancellationToken))!.Value.GetProperty("type").GetString());
        Assert.Null(await transport.ReadAsync(TestContext.Current.CancellationToken)); Assert.Equal(1, socket.CloseOutputs);
    }

    /// <summary>严格区分文本 JSON 与二进制、无效编码及不完整分片。</summary>
    [Theory]
    [InlineData("array")] [InlineData("json")] [InlineData("utf8")] [InlineData("binary")] [InlineData("partial-close")] [InlineData("abnormal-close")]
    public async Task InvalidInboundMessage_FailsConnection(string kind)
    {
        var socket = new StubWebSocket(); await using var transport = new ResponsesWebSocketTransport(socket, 100);
        switch (kind)
        {
            case "array": socket.Text("[]"); break;
            case "json": socket.Text("{"); break;
            case "utf8": socket.Frame([123, 34, 120, 34, 58, 34, 255, 34, 125]); break;
            case "binary": socket.Frame([1], WebSocketMessageType.Binary); break;
            case "partial-close": socket.Frame([123], end: false); socket.PeerClose(); break;
            case "abnormal-close": socket.PeerClose(WebSocketCloseStatus.InternalServerError); break;
        }
        var error = await Record.ExceptionAsync(async () => await transport.ReadAsync(TestContext.Current.CancellationToken));
        Assert.True(error is InvalidDataException or JsonException or WebSocketException);
        await Assert.ThrowsAsync<WebSocketException>(async () => await transport.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        Assert.Equal(0, socket.SendAttempts);
    }

    /// <summary>完整 UTF-8 字节数有明确边界，不能只限制每个片段。</summary>
    [Fact]
    public async Task MessageLimit_AppliesToAccumulatedUtf8Bytes()
    {
        var socket = new StubWebSocket(); await using var transport = new ResponsesWebSocketTransport(socket, 8);
        socket.Text("{\"x\":\"中\"}", 1);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await transport.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    /// <summary>恰好等于限制的 JSON 不被截断；过大的出站请求在 IO 前拒绝且不损坏连接。</summary>
    [Fact]
    public async Task OutboundLimit_IsLocalAndBoundaryIsInclusive()
    {
        var socket = new StubWebSocket(); var payload = new JsonObject { ["text"] = "中" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await using var transport = new ResponsesWebSocketTransport(socket, bytes.Length);
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.SendAsync(new() { ["text"] = new string('x', 100) }, TestContext.Current.CancellationToken));
        await transport.SendAsync(payload, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, Assert.Single(socket.Sent).Bytes); Assert.Equal(WebSocketMessageType.Text, socket.Sent.Single().Type); Assert.True(socket.Sent.Single().End);
    }

    /// <summary>入站达到限制后结束消息仍有效。</summary>
    [Fact]
    public async Task InboundLimit_IsInclusive()
    {
        const string json = "{\"x\":0}"; var socket = new StubWebSocket(); socket.Text(json, 1);
        await using var transport = new ResponsesWebSocketTransport(socket, Encoding.UTF8.GetByteCount(json));
        Assert.Equal(json, (await transport.ReadAsync(TestContext.Current.CancellationToken))!.Value.GetRawText());
    }

    /// <summary>读取已进入 socket 后取消，不能宣称可以从不确定的半条消息安全续读。</summary>
    [Fact]
    public async Task CancelledReceive_AbortsAndDoesNotResume()
    {
        var socket = new StubWebSocket(); await using var transport = new ResponsesWebSocketTransport(socket, 100);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = transport.ReadAsync(cancelled.Token).AsTask(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<WebSocketException>(async () => await transport.ReadAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>本地关闭或释放中断等待读取，不启动额外接收循环。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExplicitShutdown_InterruptsReadAndOwnsSocket(bool dispose)
    {
        var socket = new StubWebSocket(); var transport = new ResponsesWebSocketTransport(socket, 100);
        var pending = transport.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        if (dispose) await transport.DisposeAsync(); else await transport.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Null(await pending); await transport.DisposeAsync(); await transport.DisposeAsync(); Assert.Equal(1, socket.Disposals);
    }

    /// <summary>关闭不等待被堵塞的生成写入，直接终止不确定 IO；不会重发正文。</summary>
    [Fact]
    public async Task CloseDuringSend_AbortsWithoutWaitingForWriter()
    {
        var socket = new StubWebSocket { BeforeSend = token => Task.Delay(Timeout.Infinite, token) };
        await using var transport = new ResponsesWebSocketTransport(socket, 100);
        var pending = transport.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken).AsTask();
        await transport.CloseAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(1, socket.SendAttempts); Assert.Empty(socket.Sent);
    }

    /// <summary>只有一个原生读取者；第二个读取不能破坏已在等待的读取。</summary>
    [Fact]
    public async Task ConcurrentReceive_IsRejectedWithoutAbortingFirst()
    {
        var socket = new StubWebSocket(); await using var transport = new ResponsesWebSocketTransport(socket, 100);
        var pending = transport.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ReadAsync(TestContext.Current.CancellationToken));
        socket.Text("{}"); Assert.Equal("{}", (await pending)!.Value.GetRawText());
    }

    /// <summary>出站正文在排队前复制，多个原生写入不重叠；取消尚未开始的写入不会重发。</summary>
    [Fact]
    public async Task QueuedSend_PreservesSnapshotAndCancelledQueueIsNotSent()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new StubWebSocket { BeforeSend = token => release.Task.WaitAsync(token) };
        await using var transport = new ResponsesWebSocketTransport(socket, 1000);
        var first = transport.SendAsync(new() { ["type"] = "response.create", ["stream_id"] = "one" }, TestContext.Current.CancellationToken).AsTask();
        var snapshot = new JsonObject { ["type"] = "response.create", ["stream_id"] = "two", ["input"] = "original" };
        var second = transport.SendAsync(snapshot, TestContext.Current.CancellationToken).AsTask(); snapshot["input"] = "changed";
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var third = transport.SendAsync(new() { ["type"] = "response.create", ["stream_id"] = "cancelled" }, cancelled.Token).AsTask(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third); Assert.Equal(1, socket.SendAttempts);
        release.TrySetResult(); await Task.WhenAll(first, second);
        Assert.Equal(2, socket.SendAttempts); Assert.Equal(2, socket.Sent.Count);
        using var json = JsonDocument.Parse(socket.Sent.Last().Bytes); Assert.Equal("original", json.RootElement.GetProperty("input").GetString());
    }

    /// <summary>真正进入 socket 的写入取消使连接失败，不允许把不确定状态当成可重用连接。</summary>
    [Fact]
    public async Task CancelledActiveSend_FailsConnectionWithoutResend()
    {
        var socket = new StubWebSocket { BeforeSend = token => Task.Delay(Timeout.Infinite, token) };
        await using var transport = new ResponsesWebSocketTransport(socket, 1000);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = transport.SendAsync(new() { ["type"] = "response.create" }, cancelled.Token).AsTask(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<WebSocketException>(async () => await transport.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        Assert.Equal(1, socket.SendAttempts); Assert.Empty(socket.Sent);
    }

    /// <summary>已取消的操作没有触及 socket，连接仍可由调用者显式使用。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AlreadyCancelledOperation_DoesNotPoisonConnection(bool read)
    {
        var socket = new StubWebSocket(); await using var transport = new ResponsesWebSocketTransport(socket, 1000);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (read) await transport.ReadAsync(cancelled.Token); else await transport.SendAsync(new(), cancelled.Token);
        });
        await transport.SendAsync(new(), TestContext.Current.CancellationToken); Assert.Single(socket.Sent);
    }
}
