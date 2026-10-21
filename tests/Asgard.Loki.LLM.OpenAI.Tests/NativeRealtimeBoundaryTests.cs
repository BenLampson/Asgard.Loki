using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Tests;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.RealtimeFixture;
using static Asgard.Loki.LLM.OpenAI.Tests.CompactionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生 Realtime 的目标、取消、资源和传输边界，不虚构响应终态。</summary>
public sealed class NativeRealtimeBoundaryTests
{
    /// <summary>本地无效事件或换模型在写入之前失败，不妨碍调用者显式纠正后发送。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":null}")]
    [InlineData("{\"type\":false}")]
    [InlineData("{\"type\":\" \"}")]
    [InlineData("{\"type\":\"session.update\",\"session\":{\"model\":\"other\"}}")]
    [InlineData("{\"type\":\"response.create\",\"response\":{\"model\":\"other\"}}")]
    [InlineData("{\"type\":\"vendor.future\",\"model\":null}")]
    public async Task InvalidClientEvent_IsRejectedBeforeIo(string json)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(JsonNode.Parse(json)!.AsObject(), TestContext.Current.CancellationToken));
        var socket = Assert.Single(factory.Sockets); Assert.Equal(0, socket.SendAttempts);
        await connection.SendAsync(new() { ["type"] = "vendor.future" }, TestContext.Current.CancellationToken); Assert.Equal(1, socket.SendAttempts);
    }
    /// <summary>损坏、超限和非正常关闭不能通过正常枚举结束掩盖；不重试、不合成完成事件。</summary>
    [Theory]
    [InlineData("missing-type")] [InlineData("array")] [InlineData("binary")]
    [InlineData("oversize")] [InlineData("truncated")] [InlineData("utf8")]
    public async Task InvalidTransport_FailsExplicitly(string kind)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory, new() { MaximumMessageBytes = 100 });
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        var socket = Assert.Single(factory.Sockets);
        switch (kind)
        {
            case "missing-type": socket.Text("{\"type\":null}"); break;
            case "array": socket.Text("[]"); break;
            case "binary": socket.Frame([0], WebSocketMessageType.Binary); break;
            case "oversize": socket.Text("{\"type\":\"" + new string('a', 101) + "\"}"); break;
            case "truncated": socket.Frame(Encoding.UTF8.GetBytes("{\"type\":"), end: false); socket.PeerClose(); break;
            case "utf8": socket.Frame([0xff]); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Drain(connection, TestContext.Current.CancellationToken));
        Assert.Empty(socket.Sent); Assert.Single(factory.Calls);
    }
    /// <summary>正常关闭只表示传输结束；即使只读到增量也不创造成功响应。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NormalClose_DoesNotInventResponseCompletion(bool partial)
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        var socket = Assert.Single(factory.Sockets);
        if (partial) socket.Text("""{"type":"response.output_text.delta","response_id":"r","item_id":"i","output_index":0,"content_index":0,"delta":"partial"}""");
        socket.PeerClose(); var result = await Drain(connection, TestContext.Current.CancellationToken);
        Assert.Equal(partial ? 1 : 0, result.Count); Assert.DoesNotContain(result, item => item.Type == "response.done"); Assert.Empty(socket.Sent);
    }
    /// <summary>非正常上游关闭是传输错误，不自动恢复或换服务器。</summary>
    [Fact]
    public async Task AbnormalClose_IsTransportFailure()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        Assert.Single(factory.Sockets).PeerClose(WebSocketCloseStatus.InternalServerError);
        await Assert.ThrowsAsync<WebSocketException>(() => Drain(connection, TestContext.Current.CancellationToken)); Assert.Single(factory.Calls);
    }
    /// <summary>连接字节上限在原生 IO 前约束写入，超限不会偷偷发送或触发重试。</summary>
    [Fact]
    public async Task SendLimit_IsCheckedBeforeIo()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory, new() { MaximumMessageBytes = 60 });
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(new() { ["type"] = "future", ["payload"] = new string('a', 100) }, TestContext.Current.CancellationToken));
        Assert.Equal(0, Assert.Single(factory.Sockets).SendAttempts);
    }
    /// <summary>释放同时中断挂起的读取，且任何时候只能有一个读取者。</summary>
    [Fact]
    public async Task Dispose_InterruptsReadAndRejectsSecondReader()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        await using var first = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var pending = first.MoveNextAsync();
        await using var second = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
        await connection.DisposeAsync(); Assert.False(await pending); await connection.DisposeAsync();
        Assert.Equal(1, Assert.Single(factory.Sockets).Disposals); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>读取取消后不能继续使用不确定的传输，也不自动重连。</summary>
    [Fact]
    public async Task ReadCancellation_StopsConnectionWithoutReplay()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var read = Drain(connection, cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAsync<WebSocketException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        Assert.Single(factory.Calls); Assert.Empty(Assert.Single(factory.Sockets).Sent);
    }
    /// <summary>工厂完成时才到达的取消仍释放已取得 socket，不向调用者返回失效连接。</summary>
    [Fact]
    public async Task CancelAfterHandshake_DisposesOwnedSocket()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var socket = new StubWebSocket();
        var factory = new StubResponsesWebSocketFactory { Connect = _ => { cancel.Cancel(); return Task.FromResult<WebSocket>(socket); } };
        await using var client = RealtimeFixture.Build(factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.Realtime.OpenAsync("public", Call(), cancel.Token));
        Assert.Equal(1, socket.Disposals); Assert.Equal(0, factory.Disposals); Assert.Single(factory.Calls);
    }
    /// <summary>恢复逻辑绑定只保持目标，不伪装恢复旧 socket 或重放历史；选择器变化不能移动已有会话。</summary>
    [Fact]
    public async Task Reopen_KeepsCompleteTargetWithoutReplayingSession()
    {
        var factory = new StubResponsesWebSocketFactory(); var store = Store(); var targets = new[] { RealtimeFixture.Target("a"), RealtimeFixture.Target("b") };
        await using (var first = RealtimeFixture.Build(factory, store: store, targets: targets, selector: new TestTargetSelector(1)))
        { await using var connection = await first.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken); }
        await using var second = RealtimeFixture.Build(factory, store: store, targets: targets, selector: new TestTargetSelector(0));
        await using var next = await second.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        Assert.Equal("b", next.Metadata.TargetId); Assert.Equal(2, factory.Calls.Count);
        Assert.All(factory.Calls, call => Assert.Equal("Bearer test-key-b", call.Headers["Authorization"]));
        Assert.All(factory.Sockets, socket => Assert.Empty(socket.Sent));
    }
    /// <summary>资源或工厂语义变化使完整目标身份失效，原连接也不能继续收发。</summary>
    [Fact]
    public async Task ChangedFactoryIdentity_InvalidatesOriginalConnection()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken); factory.Version = "2";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(connection, TestContext.Current.CancellationToken));
        Assert.Empty(Assert.Single(factory.Sockets).Sent); Assert.Single(factory.Calls);
    }
    /// <summary>会话记录消失后既不能写入，也不能把正在读取的事件交给另一个绑定。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExpiredBinding_StopsIoWithoutCreatingNewBinding(bool duringRead)
    {
        var store = new ControlledSessionStore(Store()); var factory = new StubResponsesWebSocketFactory();
        await using var client = RealtimeFixture.Build(factory, store: store);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        var socket = Assert.Single(factory.Sockets);
        if (duringRead)
        {
            await using var events = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            var pending = events.MoveNextAsync(); store.ReadBinding = _ => null; socket.Text("{\"type\":\"vendor.future\"}");
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
        }
        else
        {
            store.ReadBinding = _ => null;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        }
        Assert.Equal(1, store.GetOrCreateCalls); Assert.Empty(socket.Sent); Assert.Single(factory.Calls);
    }
    /// <summary>已经进入原生写入的取消不可安全重发，连接保持明确失败状态。</summary>
    [Fact]
    public async Task SendCancellation_DoesNotReplayIndeterminateMessage()
    {
        var factory = new StubResponsesWebSocketFactory(); await using var client = RealtimeFixture.Build(factory);
        await using var connection = await client.LLM.Realtime.OpenAsync("public", Call(), TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = Assert.Single(factory.Sockets);
        socket.BeforeSend = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var send = connection.SendAsync(new() { ["type"] = "response.create" }, cancel.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await send);
        await Assert.ThrowsAsync<WebSocketException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        Assert.Equal(1, socket.SendAttempts); Assert.Empty(socket.Sent); Assert.Single(factory.Calls);
    }
}
