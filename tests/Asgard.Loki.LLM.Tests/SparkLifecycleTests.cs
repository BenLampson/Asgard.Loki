using System.Text.Json;
using Xunit;
using static Asgard.Loki.LLM.Tests.SparkCoreTests;

namespace Asgard.Loki.LLM.Tests;

/// <summary>核心只维护消息窗口所有权、取消和借用边界，不把内存适配器当真实 WebSocket 验证。</summary>
public sealed class SparkLifecycleTests
{
    /// <summary>完整消息窗口复制集合，消息自身复制 JSON；不会随原文档释放或外部集合变化丢字段。</summary>
    [Fact]
    public void CompletionOwnsImmutableFullNativeWindow()
    {
        var metadata = new LlmResultMetadata("public", "lite", "stub", "target"); SparkEvent item;
        using (var document = JsonDocument.Parse(SparkStubProvider.Part("frames")[0].GetRawText())) item = new(document.RootElement, metadata);
        var source = new List<SparkEvent> { item }; var window = new SparkCompletion(source, metadata); source.Clear(); Assert.Same(item, Assert.Single(window.Events)); Assert.True(JsonElement.DeepEquals(SparkStubProvider.Part("frames")[0], window.Events[0].Data)); Assert.Equal(metadata, window.Metadata);
        Assert.Throws<NotSupportedException>(() => ((IList<SparkEvent>)window.Events).Clear());
    }
    /// <summary>完整窗口不能为空、包含空项或混入其他目标元数据，不补造有效消息。</summary>
    [Theory] [InlineData("empty")] [InlineData("null")] [InlineData("mixed")]
    public void InvalidCompletionWindowsAreRejected(string kind)
    {
        var metadata = new LlmResultMetadata("public", "lite", "stub", "target"); var item = new SparkEvent(SparkStubProvider.Part("frames")[0], metadata with { TargetId = kind == "mixed" ? "other" : "target" });
        Assert.Throws<ArgumentException>(() => new SparkCompletion(kind == "empty" ? [] : kind == "null" ? [null!] : [item], metadata));
    }
    /// <summary>预先取消不读取或创建会话，不选择目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancellationNeverBinds(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel(); using var provider = new SparkStubProvider(); var config = Configuration(provider); var reads = 0; var store = new ControlledSessionStore(config.SessionStore!) { BeforeRead = _ => { Interlocked.Increment(ref reads); return Task.CompletedTask; } }; var selector = new ModerationTestSelector(); await using var client = new LokiClientBuilder().AddLLM(config with { SessionStore = store, TargetSelector = selector }).Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { if (stream) await foreach (var _ in client.LLM.Spark.StreamAsync(Request(), new() { SessionId = "cancel" }, cancellation.Token)) { } else await client.LLM.Spark.CompleteAsync(Request(), new() { SessionId = "cancel" }, cancellation.Token); }); Assert.Equal(0, reads); Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, selector.Calls); Assert.Empty(provider.Calls);
    }
    /// <summary>适配器忽略最后取消时，核心仍不能交付完整窗口或下一条原生消息。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalCancellationPreventsPublicDelivery(bool stream)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var provider = new SparkStubProvider { OnResult = cancellation.Cancel }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { if (stream) await foreach (var _ in client.LLM.Spark.StreamAsync(Request(), new() { SessionId = "late-cancel" }, cancellation.Token)) Assert.Fail("Cannot deliver cancelled native event"); else await client.LLM.Spark.CompleteAsync(Request(), new() { SessionId = "late-cancel" }, cancellation.Token); }); Assert.Single(provider.Calls);
    }
    /// <summary>完整/流式适配器错误保持原对象且不重试，能力不释放借用适配器。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AdapterFailureIsOneAttemptAndProviderStaysBorrowed(bool stream)
    {
        var failure = new IOException("fixture failure"); using var provider = new SparkStubProvider { Failure = failure };
        await using (var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build()) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => { if (stream) await foreach (var _ in client.LLM.Spark.StreamAsync(Request(), new() { SessionId = "failed" }, TestContext.Current.CancellationToken)) { } else await client.LLM.Spark.CompleteAsync(Request(), new() { SessionId = "failed" }, TestContext.Current.CancellationToken); }));
        Assert.Single(provider.Calls); Assert.False(provider.Disposed);
    }
    /// <summary>首条消息之后的错误原样传播，提前停止消费则关闭枚举器。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PartialStreamFailureOrEarlyBreakNeverReplays(bool fail)
    {
        var failure = new IOException("fixture partial stream failure"); using var provider = new SparkStubProvider { AfterFirstEvent = fail ? failure : null }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var count = 0;
        if (fail) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in client.LLM.Spark.StreamAsync(Request(), new() { SessionId = "partial" }, TestContext.Current.CancellationToken)) count++; }));
        else await foreach (var _ in client.LLM.Spark.StreamAsync(Request(), new() { SessionId = "partial" }, TestContext.Current.CancellationToken)) { count++; break; }
        Assert.Equal(1, count); Assert.True(provider.StreamClosed); Assert.False(provider.Disposed); Assert.Single(provider.Calls);
    }
    /// <summary>枚举器关闭期间才发生的取消仍向调用者传播，不伪装正常结束。</summary>
    [Fact]
    public async Task CancellationDuringAdapterClosureFailsEnumeration()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var provider = new SparkStubProvider { OnStreamClosed = cancellation.Cancel }; await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in client.LLM.Spark.StreamAsync(Request(), new() { SessionId = "close" }, cancellation.Token)) count++; }); Assert.Equal(4, count); Assert.True(provider.StreamClosed);
    }
}
