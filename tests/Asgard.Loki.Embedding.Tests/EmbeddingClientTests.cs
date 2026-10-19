using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.Tests;

/// <summary>通过公共模块扩展验证原生参数快照、完整目标选择和独立生命周期。</summary>
public sealed class EmbeddingClientTests
{
    /// <summary>路由和目标列表在注册时复制，首次访问前的外部修改也不能改变已注册配置。</summary>
    [Fact]
    public async Task Registration_SnapshotsRoutesAndTargets()
    {
        using var first = new StubEmbeddingAdapter("first"); using var replacement = new StubEmbeddingAdapter("replacement");
        var targets = new List<IEmbeddingAdapter> { first };
        var routes = new List<EmbeddingRoute> { new() { Model = "public", Targets = targets } };
        var builder = new LokiClientBuilder().AddEmbedding(new() { Routes = routes });
        targets[0] = replacement; routes.Clear();
        await using var client = builder.Build(); var result = await client.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal("first", result.Metadata.ProviderId); Assert.Equal("public", result.Metadata.PublicModel);
        Assert.Equal("native-first", result.Metadata.UpstreamModel); Assert.Equal(1, first.Calls); Assert.Equal(0, replacement.Calls);
        Assert.Same(client.Embedding, client.Embedding);
    }

    /// <summary>原生字段没有统一 input 假设，参数和 JsonElement 均在适配器等待之前复制。</summary>
    [Fact]
    public async Task Request_SnapshotsNativeJsonBeforeAdapterWaits()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var adapter = new StubEmbeddingAdapter("native", async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return new(JsonSerializer.SerializeToElement(request), new(request.Model, "actual", "native"));
        });
        await using var client = Build(adapter);
        var document = JsonDocument.Parse("""{"parts":[{"text":"原生😀"}],"future":null,"disabled":false}""");
        var request = Request() with { Parameters = new() { ["content"] = document.RootElement, ["taskType"] = JsonSerializer.SerializeToElement("RETRIEVAL_DOCUMENT") } };
        var call = client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken);
        document.Dispose(); request.Parameters.Clear(); release.SetResult();
        var result = await call;
        Assert.Equal("原生😀", result.Data.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("content").GetProperty("future").ValueKind);
        Assert.False(result.Data.GetProperty("content").GetProperty("disabled").GetBoolean());
        Assert.Equal("RETRIEVAL_DOCUMENT", result.Data.GetProperty("taskType").GetString()); Assert.Empty(request.Parameters);
    }

    /// <summary>各路由独立轮询完整目标，同一 builder 创建的客户端不共享轮询计数。</summary>
    [Fact]
    public async Task RoutesAndClients_HaveIndependentSelectionState()
    {
        using var first = new StubEmbeddingAdapter("first"); using var second = new StubEmbeddingAdapter("second");
        var builder = new LokiClientBuilder().AddEmbedding(new() { Routes = [
            new() { Model = "public", Targets = [first, second] }, new() { Model = "other", Targets = [first, second] }] });
        await using var one = builder.Build(); await using var two = builder.Build();
        Assert.NotSame(one.Embedding, two.Embedding);
        Assert.Equal("first", (await one.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)).Metadata.ProviderId);
        Assert.Equal("first", (await one.Embedding.EmbedAsync(Request() with { Model = "other" }, TestContext.Current.CancellationToken)).Metadata.ProviderId);
        Assert.Equal("second", (await one.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)).Metadata.ProviderId);
        Assert.Equal("first", (await two.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)).Metadata.ProviderId);
    }

    /// <summary>并发调用只选一个目标且不发生状态竞争，原生任务不获得任何会话或缓存。</summary>
    [Fact]
    public async Task ConcurrentCalls_SelectEachTargetOncePerTurn()
    {
        using var first = new StubEmbeddingAdapter("first"); using var second = new StubEmbeddingAdapter("second");
        await using var client = Build(first, second);
        var calls = Enumerable.Range(0, 100).Select(_ => Task.Run(() => client.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)));
        var results = await Task.WhenAll(calls);
        Assert.Equal(100, results.Length); Assert.Equal(50, first.Calls); Assert.Equal(50, second.Calls);
    }

    /// <summary>首个目标失败不会切换目标或重试，下一次显式调用仍可按配置选择。</summary>
    [Fact]
    public async Task Failure_DoesNotRetryOrFailOver()
    {
        var failure = new HttpRequestException("native failure");
        using var first = new StubEmbeddingAdapter("first", (_, _) => Task.FromException<EmbeddingResult>(failure));
        using var second = new StubEmbeddingAdapter("second"); await using var client = Build(first, second);
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => client.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, first.Calls); Assert.Equal(0, second.Calls);
        Assert.Equal("second", (await client.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)).Metadata.ProviderId);
    }

    /// <summary>预取消请求不调用适配器，也不消耗目标选择顺序。</summary>
    [Fact]
    public async Task PreCancellation_DoesNotSelectOrSend()
    {
        using var first = new StubEmbeddingAdapter("first"); using var second = new StubEmbeddingAdapter("second");
        await using var client = Build(first, second); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Embedding.EmbedAsync(Request(), cancellation.Token));
        Assert.Equal(0, first.Calls); Assert.Equal("first", (await client.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken)).Metadata.ProviderId);
    }

    /// <summary>调用中取消传给原目标，不重试或切换目标。</summary>
    [Fact]
    public async Task ActiveCancellation_ReachesTheSelectedAdapter()
    {
        using var first = new StubEmbeddingAdapter("first", async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
        using var second = new StubEmbeddingAdapter("second"); await using var client = Build(first, second);
        using var cancellation = new CancellationTokenSource(); var call = client.Embedding.EmbedAsync(Request(), cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.Equal(1, first.Calls); Assert.Equal(0, second.Calls);
    }

    /// <summary>释放客户端不释放借用适配器，其他客户端仍可调用同一完整目标。</summary>
    [Fact]
    public async Task ClientDisposal_DoesNotOwnAdapter()
    {
        using var adapter = new StubEmbeddingAdapter("borrowed"); var first = Build(adapter); await first.DisposeAsync();
        Assert.False(adapter.Disposed); Assert.Throws<ObjectDisposedException>(() => first.Embedding);
        await using var second = Build(adapter); await second.Embedding.EmbedAsync(Request(), TestContext.Current.CancellationToken); Assert.Equal(1, adapter.Calls);
    }

    /// <summary>模型精确匹配；未知路由、覆盖 model 和无效请求均在选择前拒绝。</summary>
    [Theory] [InlineData("case")] [InlineData("unknown")] [InlineData("override")] [InlineData("blank")] [InlineData("null-parameters")] [InlineData("undefined")]
    public async Task InvalidRequest_DoesNotCallAdapter(string kind)
    {
        using var adapter = new StubEmbeddingAdapter("native"); await using var client = Build(adapter);
        var request = Request();
        request = kind switch { "case" => request with { Model = "PUBLIC" }, "unknown" => request with { Model = "missing" },
            "blank" => request with { Model = " " }, "null-parameters" => request with { Parameters = null! },
            "undefined" => request with { Parameters = new() { ["future"] = default } },
            _ => request with { Parameters = new() { ["model"] = JsonSerializer.SerializeToElement("hidden") } } };
        if (kind is "case" or "unknown") await Assert.ThrowsAsync<InvalidOperationException>(() => client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.Calls);
    }

    /// <summary>构建仅包含嵌入模块的客户端，无 LLM、缓存或其他能力配置。</summary>
    private static Asgard.Loki.Abstractions.ILokiClient Build(params IEmbeddingAdapter[] adapters)
        => new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "public", Targets = adapters }] }).Build();
    /// <summary>输入形状由适配器定义，核心不添加字段或默认值。</summary>
    private static EmbeddingRequest Request() => new() { Model = "public" };
}
