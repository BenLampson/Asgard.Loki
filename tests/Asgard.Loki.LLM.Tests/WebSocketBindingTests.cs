using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.Tests;

/// <summary>连接打开、发送与响应归属始终使用首次协调后的完整目标。</summary>
public sealed class WebSocketBindingTests
{
    /// <summary>原生功能只能声明在 Responses 协议上。</summary>
    [Fact]
    public void WebSocketFeature_RequiresResponsesProtocol()
        => Assert.Throws<ArgumentException>(() => Build(new(), targets: [Target() with { Protocols = LlmProtocol.ChatCompletions }]));

    /// <summary>已取消的握手不建立会话或调用供应商。</summary>
    [Fact]
    public async Task CancelledOpen_DoesNotBindOrOpen()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancelled.Cancel();
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), cancelled.Token));
        Assert.Empty(provider.Opened); Assert.Equal(0, store.GetOrCreateCalls);
    }

    /// <summary>供应商握手失败直接传播，不能重试、重新选择或隐式降级 HTTP。</summary>
    [Fact]
    public async Task FailedOpen_DoesNotRetryOrFailOver()
    {
        var calls = 0; var provider = new StubResponsesWebSocketProvider
        { BeforeOpen = (_, _) => { calls++; throw new IOException("Handshake failed"); } };
        await using var client = Build(provider, targets: [Target("a"), Target("b")]);
        await Assert.ThrowsAsync<IOException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.Empty(provider.Opened);
    }

    /// <summary>缓存错误不能变成本地会话或照常发送。</summary>
    [Fact]
    public async Task CacheFailureDuringSend_PropagatesWithoutSending()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        store.ReadBinding = _ => throw new IOException("Cache unavailable");
        await Assert.ThrowsAsync<IOException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        Assert.Empty(Assert.Single(provider.Opened).Connection.Sent); Assert.Equal(1, store.GetOrCreateCalls);
    }

    /// <summary>供应商绑定语义变更后，即使缓存记录未变，旧连接也不能继续收发。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedProviderIdentity_InvalidatesExistingConnection(bool read)
    {
        var provider = new StubResponsesWebSocketProvider(); await using var client = Build(provider);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        provider.BindingVersion = "2"; var raw = Assert.Single(provider.Opened).Connection; raw.Finish();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (read) { await foreach (var _ in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { } }
            else await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        });
        Assert.Empty(raw.Sent);
    }

    /// <summary>首次选择过滤显式能力，发送模型映射后仍原样保留多流、工具、加密项和扩展字段。</summary>
    [Fact]
    public async Task OpenAndSend_UseCompleteCapableTargetAndPreserveNativeFields()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store();
        await using var client = Build(provider, store, [Target("a", LlmTargetFeatures.None), Target("b")]);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var opened = Assert.Single(provider.Opened); Assert.Equal("b", connection.Metadata.TargetId);
        Assert.Equal("key-b", opened.Context.Credential.Id); Assert.Equal("test-only-b", opened.Context.Credential.ApiKey);
        Assert.Equal("https://b.invalid/v1/", opened.Context.Target.BaseAddress.AbsoluteUri);
        var payload = JsonNode.Parse("""{"type":"response.create","model":"public","stream_id":"branch.1","generate":false,"store":false,"input":[{"type":"compaction","encrypted_content":"opaque"},{"type":"function_call_output","call_id":"call_1","output":"ok"}],"future":{"keep":null}}""")!.AsObject();
        var before = payload.ToJsonString();
        await connection.SendAsync(payload, TestContext.Current.CancellationToken);
        Assert.Equal(before, payload.ToJsonString()); var sent = Assert.Single(opened.Connection.Sent);
        var expected = payload.DeepClone().AsObject(); expected["model"] = "model-b";
        Assert.True(JsonNode.DeepEquals(expected, sent)); Assert.Equal(1, store.GetOrCreateCalls);
    }

    /// <summary>已有会话不能为了支持 WebSocket 而切换到另一目标。</summary>
    [Fact]
    public async Task ExistingHttpSession_CannotSwitchTargetsForWebSocket()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store();
        await using var client = Build(provider, store, [Target("a", LlmTargetFeatures.None), Target("b")]);
        await client.LLM.Responses.CreateAsync(new() { Model = "public" }, Call(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Empty(provider.Opened); Assert.Equal("a", (await store.GetAsync("session", TestContext.Current.CancellationToken))!.TargetId);
    }

    /// <summary>没有显式声明功能时不试探握手，也不创建会话。</summary>
    [Fact]
    public async Task MissingFeature_DoesNotBindOrOpen()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store();
        await using var client = Build(provider, store, [Target(features: LlmTargetFeatures.None)]);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Empty(provider.Opened); Assert.Equal(0, store.GetOrCreateCalls);
    }

    /// <summary>并发首次打开经过显式协调后只能拿到同一个完整目标。</summary>
    [Fact]
    public async Task ConcurrentOpens_UseOneWinningCompleteTarget()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store();
        var tasks = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await using var client = Build(provider, store, [Target("a"), Target("b")], new TestTargetSelector(index));
            await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
            return connection.Metadata;
        }, TestContext.Current.CancellationToken));
        var results = await Task.WhenAll(tasks); Assert.Single(results.Distinct());
        Assert.Single(provider.Opened.Select(item => item.Context.Credential.Id).Distinct());
        Assert.Single(provider.Opened.Select(item => item.Context.Target.BaseAddress).Distinct()); Assert.Equal(16, provider.Opened.Count);
    }

    /// <summary>握手过程中会话消失时立即释放所得连接，不把它交给调用者使用。</summary>
    [Fact]
    public async Task SessionLostDuringOpen_DisposesConnectionWithoutRebinding()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store();
        provider.BeforeOpen = (_, _) => { store.ReadBinding = _ => null; return Task.CompletedTask; };
        await using var client = Build(provider, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(provider.Opened).Connection.DisposeCount); Assert.Equal(1, store.GetOrCreateCalls);
    }

    /// <summary>原生连接宣称不同目标时不能用公共元数据掩盖错误。</summary>
    [Fact]
    public async Task WrongConnectionMetadata_IsRejectedAndDisposed()
    {
        var provider = new StubResponsesWebSocketProvider { OverrideMetadata = new("public", "wrong", "ws", "b") };
        await using var client = Build(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(provider.Opened).Connection.DisposeCount);
    }

    /// <summary>连接打开后会话丢失或完整身份被替换时，发送不得创建新绑定或调用上游。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Send_RejectsLostOrReplacedSession(bool replace)
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        store.ReadBinding = old => replace ? old! with { TargetId = "b", Fingerprint = "different" } : null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken));
        Assert.Empty(Assert.Single(provider.Opened).Connection.Sent); Assert.Equal(1, store.GetOrCreateCalls);
    }

    /// <summary>不同命名流可复用当前会话响应；其他会话响应仍严格拒绝。</summary>
    [Fact]
    public async Task IncomingResponseIds_AreOwnedBySessionAcrossLanes()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var raw = Assert.Single(provider.Opened).Connection;
        raw.Emit("""{"type":"response.created","stream_id":"one","response":{"id":"resp_ws","future":"keep"}}""");
        raw.Emit("""{"type":"vendor.future","stream_id":"two","response":{"id":"not_a_response"},"opaque":null}""");
        raw.Emit("""{"type":"response.completed","stream_id":"one","response":{"id":"resp_ws","status":"completed","output":[]}}"""); raw.Finish();
        var items = new List<ResponseEvent>(); await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) items.Add(item);
        Assert.Equal(3, items.Count); Assert.Equal("keep", items[0].Data.GetProperty("response").GetProperty("future").GetString());
        Assert.Equal("vendor.future", items[1].Type); Assert.All(items, item => Assert.Equal(connection.Metadata, item.Metadata));
        var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
        Assert.True(await store.OwnsResponseAsync(binding, "resp_ws", TestContext.Current.CancellationToken));
        Assert.False(await store.OwnsResponseAsync(binding, "not_a_response", TestContext.Current.CancellationToken));
        await connection.SendAsync(new() { ["type"] = "response.create", ["stream_id"] = "fork", ["previous_response_id"] = "resp_ws" }, TestContext.Current.CancellationToken);
        await using var other = await client.LLM.Responses.OpenWebSocketAsync("public", Call("other"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await other.SendAsync(new() { ["type"] = "response.create", ["previous_response_id"] = "resp_ws" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await other.SendAsync(new() { ["type"] = "response.cancel", ["response_id"] = "resp_ws" }, TestContext.Current.CancellationToken));
        await connection.SendAsync(new() { ["type"] = "response.cancel", ["response_id"] = "resp_ws", ["event_id"] = "cancel-own" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, raw.Sent.Count); Assert.Empty(provider.Opened.Last().Connection.Sent);
        var http = await client.LLM.Responses.CreateAsync(new() { Model = "public", PreviousResponseId = "resp_ws" }, Call(), TestContext.Current.CancellationToken);
        Assert.Equal(connection.Metadata, http.Metadata);
    }

    /// <summary>生命周期事件到达时重新检查原绑定，不能给过期或替换后的会话登记响应。</summary>
    [Fact]
    public async Task SessionLostDuringRead_DoesNotRegisterResponse()
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        await using var reader = connection.ReadEventsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var pending = reader.MoveNextAsync().AsTask(); store.ReadBinding = _ => null;
        Assert.Single(provider.Opened).Connection.Emit("""{"type":"response.created","response":{"id":"expired"}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        store.ReadBinding = null; var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
        Assert.False(await store.OwnsResponseAsync(binding, "expired", TestContext.Current.CancellationToken)); Assert.Equal(1, store.GetOrCreateCalls);
    }
}
