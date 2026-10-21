using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>Realtime 有界严格 JSON 分帧、取消和显式关闭；不推断响应成功。</summary>
public sealed class AzureRealtimeTransportTests
{
    /// <summary>非法类型和换模型在本地失败，之后仍允许显式纠正。</summary>
    [Theory] [InlineData("{}")] [InlineData("{\"type\":null}")] [InlineData("{\"type\":false}")] [InlineData("{\"type\":\" \"}")]
    [InlineData("{\"type\":\"session.update\",\"session\":{\"model\":\"other\"}}")]
    [InlineData("{\"type\":\"response.create\",\"response\":{\"model\":\"other\"}}")]
    public async Task InvalidLocalEventsDoNotSend(string json)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory)); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(JsonNode.Parse(json)!.AsObject(), TestContext.Current.CancellationToken)); Assert.Equal(0, socket.SendAttempts);
        await connection.SendAsync(new() { ["type"] = "future.native" }, TestContext.Current.CancellationToken); Assert.Equal(1, socket.SendAttempts);
    }
    /// <summary>未定义位置的 model 是业务原文，不遍历工具、转录或未来事件内容去选择部署。</summary>
    [Theory] [InlineData("future.native")] [InlineData("conversation.item.create")] [InlineData("session.update")] [InlineData("response.create")]
    public async Task UnknownModelFieldsRemainBusinessData(string type)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var connection = await AzureRealtimeFixture.Provider(http, factory).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken);
        var payload = new JsonObject { ["type"] = type, ["model"] = "unmapped-root-business", ["future"] = new JsonObject { ["model"] = "unmapped-inner-business" } }; var before = payload.ToJsonString();
        await connection.SendAsync(payload, TestContext.Current.CancellationToken); Assert.Equal(before, Encoding.UTF8.GetString(Assert.Single(Assert.Single(factory.Sockets).Sent).Bytes));
    }
    /// <summary>未知字段内的非法 CLR Unicode 在 socket 前失败，不能由序列化器替代。</summary>
    [Fact]
    public async Task InvalidOriginalUnicodeCannotReachSocket()
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var connection = await AzureRealtimeFixture.Provider(http, factory).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<EncoderFallbackException>(async () => await connection.SendAsync(new() { ["type"] = "future.native", ["future"] = "\ud800" }, TestContext.Current.CancellationToken)); Assert.Equal(0, Assert.Single(factory.Sockets).SendAttempts);
    }
    /// <summary>非法 UTF-8/转义/重复属性、二进制和截断分帧全部明确失败并关闭资源。</summary>
    [Theory] [InlineData("missing-type")] [InlineData("array")] [InlineData("binary")] [InlineData("oversize")]
    [InlineData("truncated")] [InlineData("utf8")] [InlineData("unicode")] [InlineData("duplicate")] [InlineData("broken-json")] [InlineData("abnormal-close")]
    public async Task InvalidNativeTransportNeverEndsAsSuccess(string kind)
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory, maximum: 100)); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        switch (kind)
        {
            case "missing-type": socket.Text("{\"type\":null}"); break;
            case "array": socket.Text("[]"); break;
            case "binary": socket.Frame([0], WebSocketMessageType.Binary); break;
            case "oversize": socket.Text("{\"type\":\"" + new string('a', 101) + "\"}", 3); break;
            case "truncated": socket.Frame(Encoding.UTF8.GetBytes("{\"type\":"), end: false); socket.PeerClose(); break;
            case "utf8": socket.Frame([255]); break;
            case "unicode": socket.Text("{\"type\":\"future.native\",\"x\":\"\\ud800\"}"); break;
            case "duplicate": socket.Text("{\"type\":\"future.native\",\"x\":{\"same\":1,\"same\":2}}"); break;
            case "broken-json": socket.Text("{\"type\":"); break;
            default: socket.PeerClose(WebSocketCloseStatus.InternalServerError); break;
        }
        var error = await Record.ExceptionAsync(() => AzureRealtimeFixture.Drain(connection, TestContext.Current.CancellationToken)); Assert.NotNull(error);
        if (kind == "abnormal-close") Assert.IsType<WebSocketException>(error); else if (kind is "broken-json" or "duplicate") Assert.IsAssignableFrom<JsonException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.Equal(1, socket.Disposals); Assert.Single(factory.Calls); Assert.Empty(socket.Sent); Assert.Equal(0, http.Creates);
    }
    /// <summary>完整消息原始 UTF-8 字节恰到上限成功，少一字节在相应方向失败，不截断。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExactMessageByteBoundary(bool send, bool below)
    {
        const string raw = "{\"type\":\"future.native\",\"exact\":9007199254740993,\"flag\":false}"; var bytes = Encoding.UTF8.GetBytes(raw); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory();
        await using var connection = await AzureRealtimeFixture.Provider(http, factory, maximum: bytes.Length - (below ? 1 : 0)).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        if (send)
        {
            if (below) await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(JsonNode.Parse(raw)!.AsObject(), TestContext.Current.CancellationToken));
            else { await connection.SendAsync(JsonNode.Parse(raw)!.AsObject(), TestContext.Current.CancellationToken); Assert.Equal(bytes, Assert.Single(socket.Sent).Bytes); } Assert.Equal(below ? 0 : 1, socket.SendAttempts);
        }
        else
        {
            socket.Text(raw, 1); socket.PeerClose(); if (below) await Assert.ThrowsAsync<InvalidDataException>(() => AzureRealtimeFixture.Drain(connection, TestContext.Current.CancellationToken));
            else Assert.Equal(raw, Assert.Single(await AzureRealtimeFixture.Drain(connection, TestContext.Current.CancellationToken)).Data.GetRawText());
        }
    }
    /// <summary>单读取者规则和显式释放可以中断挂起读取，不制造模型终态。</summary>
    [Fact]
    public async Task DisposeInterruptsReadAndRejectsSecondReader()
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory)); var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken);
        await using var first = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken); var pending = first.MoveNextAsync(); await using var second = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync()); await connection.DisposeAsync(); Assert.False(await pending); await connection.DisposeAsync(); Assert.Equal(1, Assert.Single(factory.Sockets).Disposals); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>实际读写中的取消停止不确定的传输，不允许重发或继续读写。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationDuringIoStopsWithoutReplay(bool send)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var connection = await AzureRealtimeFixture.Provider(http, factory).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        if (send) socket.BeforeSend = token => { cancellation.Cancel(); return Task.Delay(Timeout.Infinite, token); };
        var operation = send ? connection.SendAsync(new() { ["type"] = "response.create" }, cancellation.Token).AsTask() : AzureRealtimeFixture.Drain(connection, cancellation.Token);
        if (!send) cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await Assert.ThrowsAsync<WebSocketException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken)); Assert.Single(factory.Calls); Assert.Empty(socket.Sent); Assert.Equal(1, socket.Disposals);
    }
    /// <summary>原始请求在首次异步 socket 等待前冻结，不受外部 DOM 修改影响。</summary>
    [Fact]
    public async Task CallerMutationCannotChangeInFlightMessage()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory();
        await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory)); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets); socket.BeforeSend = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var message = new JsonObject { ["type"] = "future.native", ["payload"] = "original" }; var pending = connection.SendAsync(message, TestContext.Current.CancellationToken).AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); message.Clear(); release.SetResult(); await pending;
        Assert.Equal("{\"type\":\"future.native\",\"payload\":\"original\"}", Encoding.UTF8.GetString(Assert.Single(socket.Sent).Bytes));
    }
    /// <summary>即使原生 socket 返回数据后才取消，也不交付本次读写成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateCancellationAfterNativeIoPreventsPublication(bool send)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory();
        await using var connection = await AzureRealtimeFixture.Provider(http, factory).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        if (send) { socket.AfterSend = cancellation.Cancel; await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection.SendAsync(new() { ["type"] = "future.native" }, cancellation.Token)); }
        else { socket.AfterReceive = cancellation.Cancel; socket.Text("{\"type\":\"future.native\"}"); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AzureRealtimeFixture.Drain(connection, cancellation.Token)); }
        Assert.Equal(1, socket.Disposals); Assert.Single(factory.Calls); Assert.Equal(send ? 1 : 0, socket.SendAttempts);
    }
    /// <summary>显式关闭的原生 IO 失败明确报错且关闭本地资源，不返回成功关闭。</summary>
    [Fact]
    public async Task CloseFailureStillReleasesOwnedSocket()
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var connection = await AzureRealtimeFixture.Provider(http, factory).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets); socket.CloseError = new IOException("fixture close failure");
        await Assert.ThrowsAsync<IOException>(async () => await connection.CloseAsync(TestContext.Current.CancellationToken)); Assert.Equal(1, socket.Disposals); Assert.Single(factory.Calls);
    }

    /// <summary>排队之外已进入 socket 的发送被显式关闭中断，不等待隐藏接收循环或重放。</summary>
    [Fact]
    public async Task CloseDuringPendingSendStopsWithoutReplay()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var client = AzureRealtimeFixture.Build(AzureRealtimeFixture.Provider(http, factory)); await using var connection = await client.LLM.Realtime.OpenAsync("public", AzureRealtimeFixture.Call(), TestContext.Current.CancellationToken); var socket = Assert.Single(factory.Sockets);
        socket.BeforeSend = token => { entered.SetResult(); return Task.Delay(Timeout.Infinite, token); }; var pending = connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken).AsTask(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await connection.CloseAsync(TestContext.Current.CancellationToken); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Empty(socket.Sent); Assert.Equal(1, socket.Disposals); Assert.Single(factory.Calls);
    }
    /// <summary>未知对象中的重复字段也不能写入原生协议。</summary>
    [Fact]
    public async Task DuplicateUnknownRequestFailsBeforeNativeSend()
    {
        using var http = AzureCompactionFixture.Factory(); using var factory = new AzureRealtimeFactory(); await using var connection = await AzureRealtimeFixture.Provider(http, factory).OpenRealtimeAsync(AzureRealtimeFixture.Context(), TestContext.Current.CancellationToken);
        var payload = JsonNode.Parse("{\"type\":\"future.native\",\"future\":{\"same\":1,\"same\":2}}")!.AsObject();
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await connection.SendAsync(payload, TestContext.Current.CancellationToken)); Assert.Equal(0, Assert.Single(factory.Sockets).SendAttempts);
    }

}
