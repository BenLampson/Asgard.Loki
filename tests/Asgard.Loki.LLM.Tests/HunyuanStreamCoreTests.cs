using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Tests.HunyuanCoreTests;

namespace Asgard.Loki.LLM.Tests;

/// <summary>混元原生事件、异步快照、取消和流资源交付边界；不模拟供应商状态机。</summary>
public sealed class HunyuanStreamCoreTests
{
    /// <summary>流式方法保留原生工具分片、终态与尾部字段，不包信封或补 DONE。</summary>
    [Fact]
    public async Task NativeStreamRetainsAllGoldenEventsAndSseMetadata()
    {
        using var provider = new HunyuanStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); var before = JsonSerializer.Serialize(request); List<HunyuanEvent> events = [];
        await foreach (var item in client.LLM.Hunyuan.StreamAsync(request, new() { SessionId = "stream" }, TestContext.Current.CancellationToken)) events.Add(item);
        var call = Assert.Single(provider.Calls); Assert.True(JsonElement.DeepEquals(HunyuanStubProvider.Fixture("wire_stream"), JsonSerializer.SerializeToElement(call.Body))); Assert.Equal(before, JsonSerializer.Serialize(request));
        var golden = HunyuanStubProvider.Fixture("events").EnumerateArray().ToArray(); Assert.Equal(golden.Length, events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            Assert.True(JsonElement.DeepEquals(golden[i], events[i].Data)); Assert.Equal("native.delta", events[i].EventName); Assert.Equal("native-id", events[i].EventId); Assert.Equal(600, events[i].RetryMilliseconds); Assert.Equal(new LlmResultMetadata("public", "native-fixed", provider.Id, "a"), events[i].Metadata);
        }
        Assert.False(events[0].Data.GetProperty("Choices")[0].TryGetProperty("Index", out _)); Assert.Equal("tool_calls", events[3].Data.GetProperty("Choices")[0].GetProperty("FinishReason").GetString()); Assert.True(provider.StreamClosed);
    }
    /// <summary>完整/流式请求在首次实际缓存等待前已冻结，包括消息、工具和嵌套未知字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotPrecedesAsynchronousCacheRead(bool stream)
    {
        using var provider = new HunyuanStubProvider(); var options = Configuration(provider); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledSessionStore(options.SessionStore!) { BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store }).Build(); var request = Request(); var token = TestContext.Current.CancellationToken;
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.Hunyuan.StreamAsync(request, new() { SessionId = "wait" }, token)) { } else await client.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "wait" }, token); }
        var pending = Invoke(); await entered.Task.WaitAsync(token); request.Parameters.Clear(); release.SetResult(); await pending;
        Assert.True(JsonElement.DeepEquals(HunyuanStubProvider.Fixture(stream ? "wire_stream" : "wire_complete"), JsonSerializer.SerializeToElement(Assert.Single(provider.Calls).Body))); Assert.Equal(1, store.GetOrCreateCalls);
    }
    /// <summary>大小写覆盖、Undefined 和原始 Unicode 错误先于缓存、选择和适配器调用失败。</summary>
    [Theory] [InlineData(false, "Model")] [InlineData(true, "Model")] [InlineData(false, "model")] [InlineData(true, "MODEL")]
    [InlineData(false, "Stream")] [InlineData(true, "Stream")] [InlineData(false, "stream")] [InlineData(true, "STREAM")]
    [InlineData(false, "undefined")] [InlineData(true, "undefined")] [InlineData(false, "unicode-model")] [InlineData(true, "unicode-model")]
    [InlineData(false, "unicode-key")] [InlineData(true, "unicode-key")] [InlineData(false, "unicode-value")] [InlineData(true, "unicode-value")]
    public async Task InvalidRequestsFailBeforeBinding(bool stream, string field)
    {
        using var provider = new HunyuanStubProvider(); var options = Configuration(provider); var store = new ControlledSessionStore(options.SessionStore!); var selector = new ModerationTestSelector(); await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store, TargetSelector = selector }).Build(); var request = Request();
        if (field == "unicode-model") request = request with { Model = "\ud800" };
        else if (field == "unicode-key") request.Parameters["\ud800"] = JsonSerializer.SerializeToElement(false);
        else if (field == "unicode-value") request.Parameters["Future"] = JsonSerializer.Deserialize<JsonElement>("\"\\ud800\"");
        else request.Parameters[field] = field == "undefined" ? default : JsonSerializer.SerializeToElement(false);
        var error = await Record.ExceptionAsync(async () => { if (stream) await foreach (var _ in client.LLM.Hunyuan.StreamAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken)) { } else await client.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken); });
        Assert.NotNull(error); if (!field.StartsWith("unicode", StringComparison.Ordinal)) Assert.IsType<ArgumentException>(error);
        Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, selector.Calls); Assert.Empty(provider.Calls);
    }
    /// <summary>供应商忽略最后取消时，核心仍阻止完整结果和下一条事件交付。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalCancellationPreventsDelivery(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var provider = new HunyuanStubProvider { OnResult = cancellation.Cancel }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.Hunyuan.StreamAsync(Request(), new() { SessionId = "cancel" }, cancellation.Token)) Assert.Fail("Cannot publish cancelled event"); else await client.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = "cancel" }, cancellation.Token); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke); Assert.Single(provider.Calls);
    }
    /// <summary>取消发生在适配器流关闭时也不能把枚举完成报告为成功。</summary>
    [Fact]
    public async Task CancellationDuringStreamClosureStillFailsEnumeration()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var provider = new HunyuanStubProvider { OnStreamClosed = cancellation.Cancel }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in client.LLM.Hunyuan.StreamAsync(Request(), new() { SessionId = "close-cancel" }, cancellation.Token)) count++; }); Assert.Equal(6, count); Assert.True(provider.StreamClosed); Assert.Single(provider.Calls);
    }
    /// <summary>调用者提前结束枚举会关闭适配器枚举器，不释放借用供应商。</summary>
    [Fact]
    public async Task EarlyBreakClosesNativeEnumerator()
    {
        using var provider = new HunyuanStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        await foreach (var _ in client.LLM.Hunyuan.StreamAsync(Request(), new() { SessionId = "early" }, TestContext.Current.CancellationToken)) break;
        Assert.True(provider.StreamClosed); Assert.False(provider.Disposed); Assert.Single(provider.Calls);
    }
    /// <summary>首个事件后的错误保留原异常，不重放先前事件或换目标。</summary>
    [Fact]
    public async Task TailFailureRemainsOriginalAndDoesNotRetry()
    {
        var failure = new IOException("fixture stream failure"); using var provider = new HunyuanStubProvider { AfterFirstEvent = failure }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var count = 0;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in client.LLM.Hunyuan.StreamAsync(Request(), new() { SessionId = "failure" }, TestContext.Current.CancellationToken)) count++; })); Assert.Equal(1, count); Assert.True(provider.StreamClosed); Assert.Single(provider.Calls);
    }
    /// <summary>预先取消的完整/流式调用不会读取缓存或选择目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancelledCallNeverBinds(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel(); using var provider = new HunyuanStubProvider(); var options = Configuration(provider); var store = new ControlledSessionStore(options.SessionStore!); var selector = new ModerationTestSelector(); await using var client = new LokiClientBuilder().AddLLM(options with { SessionStore = store, TargetSelector = selector }).Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { if (stream) await foreach (var _ in client.LLM.Hunyuan.StreamAsync(Request(), new() { SessionId = "cancelled" }, cancellation.Token)) { } else await client.LLM.Hunyuan.CompleteAsync(Request(), new() { SessionId = "cancelled" }, cancellation.Token); }); Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, selector.Calls); Assert.Empty(provider.Calls);
    }
    /// <summary>核心的 JSON 快照保留嵌套重复原生属性，不通过归一化改写未知数据。</summary>
    [Fact]
    public async Task UnknownNestedDuplicateFieldsRemainInSnapshot()
    {
        using var provider = new HunyuanStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); request.Parameters["Future"] = JsonSerializer.Deserialize<JsonElement>("{\"duplicate\":1,\"duplicate\":2}");
        await client.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "duplicates" }, TestContext.Current.CancellationToken);
        var native = JsonSerializer.SerializeToElement(Assert.Single(provider.Calls).Body).GetProperty("Future"); Assert.Equal(new[] { 1, 2 }, native.EnumerateObject().Select(p => p.Value.GetInt32()));
    }
}
