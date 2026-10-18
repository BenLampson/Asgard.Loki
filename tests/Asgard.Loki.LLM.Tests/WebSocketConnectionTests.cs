using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.Tests;

/// <summary>连接边界的本地验证、输入快照、并发、取消和所有权。</summary>
public sealed class WebSocketConnectionTests
{
    /// <summary>无效协议字段不能发送；不静默删掉 stream/background 或更换模型。</summary>
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"type":null}""")]
    [InlineData("""{"type":"response.create","model":"other"}""")]
    [InlineData("""{"type":"response.create","model":null}""")]
    [InlineData("""{"type":"response.create","stream":false}""")]
    [InlineData("""{"type":"response.create","background":null}""")]
    [InlineData("""{"type":"response.create","previous_response_id":123}""")]
    [InlineData("""{"type":"response.create","previous_response_id":" "}""")]
    public async Task InvalidCreate_IsRejectedBeforeSend(string json)
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var error = await Record.ExceptionAsync(async () => await connection.SendAsync(JsonNode.Parse(json)!.AsObject(), TestContext.Current.CancellationToken));
        Assert.True(error is ArgumentException or InvalidOperationException); Assert.Empty(Assert.Single(provider.Opened).Connection.Sent);
    }

    /// <summary>即使排队等待已有发送，输入仍是调用发生时的独立快照；实际写入没有重叠。</summary>
    [Fact]
    public async Task QueuedSend_CopiesBeforeAwaitAndSerializesWrites()
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var raw = Assert.Single(provider.Opened).Connection;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var writers = 0;
        raw.BeforeSend = async (_, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref writers)); entered.TrySetResult();
            try { await release.Task.WaitAsync(token); } finally { Interlocked.Decrement(ref writers); }
        };
        var first = connection.SendAsync(new() { ["type"] = "response.create", ["stream_id"] = "one" }, TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondPayload = JsonNode.Parse("""{"type":"response.create","stream_id":"two","input":[{"future":"original"}]}""")!.AsObject();
        var second = connection.SendAsync(secondPayload, TestContext.Current.CancellationToken).AsTask();
        secondPayload["input"]![0]!["future"] = "mutated"; release.TrySetResult(); await Task.WhenAll(first, second);
        Assert.Equal(2, raw.Sent.Count); Assert.Equal("original", raw.Sent.Last()["input"]![0]!["future"]!.GetValue<string>());
        Assert.Equal("two", raw.Sent.Last()["stream_id"]!.GetValue<string>());
    }

    /// <summary>等待写锁时取消不会发送，后续显式调用仍能使用原连接。</summary>
    [Fact]
    public async Task CancellationWhileQueued_DoesNotSendOrReplay()
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var raw = Assert.Single(provider.Opened).Connection; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.BeforeSend = (_, token) => new(release.Task.WaitAsync(token));
        var first = connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken).AsTask();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = connection.SendAsync(new() { ["type"] = "response.create", ["stream_id"] = "cancelled" }, cancelled.Token).AsTask(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting); release.TrySetResult(); await first;
        raw.BeforeSend = null; await connection.SendAsync(new() { ["type"] = "future.control", ["event_id"] = "control", ["unknown"] = false }, TestContext.Current.CancellationToken);
        Assert.Equal(2, raw.Sent.Count); Assert.Equal("future.control", raw.Sent.Last()["type"]!.GetValue<string>());
        Assert.False(raw.Sent.Last()["unknown"]!.GetValue<bool>()); Assert.False(raw.Sent.Last().ContainsKey("model"));
    }

    /// <summary>同时只能有一个读取者，取消等待后可以重新显式读取；读和写仍可并行。</summary>
    [Fact]
    public async Task Reader_IsExclusiveAndCancellationReleasesIt()
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var first = connection.ReadEventsAsync(cancelled.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken); var pending = first.MoveNextAsync().AsTask();
        await using var competing = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await competing.MoveNextAsync());
        await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var raw = Assert.Single(provider.Opened).Connection; raw.Emit("""{"type":"vendor.future","stream_id":"lane","data":null}"""); raw.Finish();
        var events = new List<ResponseEvent>(); await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Single(events); Assert.Equal("lane", events[0].Data.GetProperty("stream_id").GetString()); Assert.Single(raw.Sent);
    }

    /// <summary>失败、不完整及流级错误是原生事件，不伪装成成功或清除其他流身份。</summary>
    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("error")]
    public async Task FailureEvents_ArePreserved(string type)
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var raw = Assert.Single(provider.Opened).Connection;
        var data = new JsonObject { ["type"] = type, ["stream_id"] = "failed-lane", ["response"] = new JsonObject { ["id"] = "failure", ["future"] = true }, ["error"] = new JsonObject { ["code"] = "native_code" } };
        raw.Emit(data.ToJsonString()); raw.Finish(); var events = new List<ResponseEvent>();
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) events.Add(item);
        Assert.True(JsonNode.DeepEquals(data, JsonNode.Parse(Assert.Single(events).Data.GetRawText())));
    }

    /// <summary>损坏的生命周期 ID 不登记为归属，也不继续伪造合法事件。</summary>
    [Theory]
    [InlineData("""{"type":"response.created"}""")]
    [InlineData("""{"type":"response.completed","response":{"id":" "}}""")]
    [InlineData("""{"type":"response.failed","response":{"id":4}}""")]
    public async Task InvalidLifecycleResponseId_IsRejected(string json)
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var raw = Assert.Single(provider.Opened).Connection; raw.Emit(json); raw.Finish();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) Assert.Fail("无效生命周期不能返回给调用者。");
        });
    }

    /// <summary>显式关闭和释放只处理连接，不能销毁借用的供应商或缓存。</summary>
    [Fact]
    public async Task CloseAndDispose_AreIdempotentAndSessionSurvives()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await connection.CloseAsync(TestContext.Current.CancellationToken); await connection.CloseAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        await connection.DisposeAsync(); await connection.DisposeAsync(); var raw = Assert.Single(provider.Opened).Connection;
        Assert.Equal(1, raw.CloseCount); Assert.Equal(1, raw.DisposeCount); Assert.NotNull(await store.GetAsync("session", TestContext.Current.CancellationToken));
        await using var next = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        Assert.Equal(connection.Metadata, next.Metadata); Assert.Equal(1, store.GetOrCreateCalls);
    }
}
