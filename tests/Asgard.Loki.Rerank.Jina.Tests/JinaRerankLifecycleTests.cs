using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Jina.Tests;

/// <summary>目标配置、原生请求快照、HTTP 取消及借用资源生命周期回归。</summary>
public sealed class JinaRerankLifecycleTests
{
    /// <summary>错误目标在任何 HTTP 客户端创建前失败。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")]
    [InlineData("fragment")] [InlineData("null-root")] [InlineData("id")] [InlineData("model")]
    [InlineData("key")] [InlineData("name")]
    public void InvalidConfiguration_FailsWithoutHttp(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedRerankFactory(handler);
        var root = kind switch { "relative" => new Uri("v1/", UriKind.Relative), "scheme" => new Uri("ftp://rank.invalid/"),
            "userinfo" => new Uri("https://user:pass@rank.invalid/"), "query" => new Uri("https://rank.invalid/v1/?key=x"),
            "fragment" => new Uri("https://rank.invalid/v1/#x"), "null-root" => null!, _ => new Uri("https://rank.invalid/v1/") };
        Assert.ThrowsAny<ArgumentException>(() => new JinaRerankAdapter(factory, new()
        { Id = kind == "id" ? " " : "id", Model = kind == "model" ? " " : "actual", ApiKey = kind == "key" ? " " : "key",
            HttpClientName = kind == "name" ? " " : "native-http", BaseAddress = root }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>不硬编码服务地址，不改模型名；版本/租户路径保留，凭据不进入选项 JSON。</summary>
    [Theory] [InlineData("https://rank.invalid/tenant/v1")] [InlineData("https://rank.invalid/tenant/v1/")]
    public async Task CompleteTarget_PreservesEndpointAndModel(string root)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://rank.invalid/tenant/v1/rerank", message.RequestUri!.AbsoluteUri); Assert.Equal("fixture-private", message.Headers.Authorization!.Parameter);
            using var wire = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.Equal("caller-authorized-custom-model", wire.RootElement.GetProperty("model").GetString());
            Assert.False(message.Headers.Contains("X-Client-Name")); return JinaRerankFixtures.Json(JinaRerankFixtures.Read("text"));
        });
        using var factory = new BorrowedRerankFactory(handler); var options = new JinaRerankOptions
        { Model = "caller-authorized-custom-model", ApiKey = "fixture-private", BaseAddress = new(root), HttpClientName = "custom-http" };
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(options));
        var result = await new JinaRerankAdapter(factory, options).RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("caller-authorized-custom-model", result.Metadata.UpstreamModel); Assert.Equal("returned-version", result.Data.GetProperty("model").GetString());
        Assert.Equal("custom-http", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>直接调用也在首次 await 前保存图片查询和候选文档快照，不受外部字典修改和 JSON 释放影响。</summary>
    [Fact]
    public async Task DirectCall_SnapshotsNativeMediaBeforeAwait()
    {
        using var source = JsonDocument.Parse(JinaRerankFixtures.Read("multimodal-image-query", "request").ToJsonString());
        var request = JinaRerankFixtures.Request("multimodal-image-query"); request.Parameters["query"] = source.RootElement.GetProperty("query"); request.Parameters["documents"] = source.RootElement.GetProperty("documents");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token);
            Assert.True(JsonNode.DeepEquals(JinaRerankFixtures.Read("multimodal-image-query", "wire"), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return JinaRerankFixtures.Json(JinaRerankFixtures.Read("multimodal-image-query"));
        });
        var call = JinaRerankFixtures.Adapter(handler).RerankAsync(request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); source.Dispose(); request.Parameters.Clear(); release.SetResult();
        var result = await call; Assert.Equal("A😀", result.JinaRankings[0].Document!.Value.GetString());
    }

    /// <summary>预取消不创建 HTTP 客户端。</summary>
    [Fact]
    public async Task PreCancelledCall_DoesNotCreateClient()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedRerankFactory(handler);
        var adapter = new JinaRerankAdapter(factory, new() { Model = "actual", ApiKey = "key" });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RerankAsync(JinaRerankFixtures.Request(), cancellation.Token));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>发送阶段取消直接传给原生 HTTP，不重试或变更模型。</summary>
    [Fact]
    public async Task ActiveCancellation_ReachesHttp()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.Equal(1, handler.Calls);
    }

    /// <summary>正文读取阶段取消释放响应，不把部分 JSON 包装为成功。</summary>
    [Fact]
    public async Task BodyCancellation_DisposesResponse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new TrackedRerankContent("", async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>正文忽略取消并返回完整 JSON 时，适配器仍必须在返回前检查取消。</summary>
    [Fact]
    public async Task CancellationAfterBodyWrite_IsNotSuccess()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new TrackedRerankContent("", async (stream, _) =>
        { await stream.WriteAsync(Encoding.UTF8.GetBytes(JinaRerankFixtures.Read("text").ToJsonString()), TestContext.Current.CancellationToken); cancellation.Cancel(); });
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), cancellation.Token));
        Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>成功、协议失败或 HTTP 失败均释放本次内容；模块不释放调用者的工厂与处理器。</summary>
    [Theory] [InlineData("success")] [InlineData("invalid")] [InlineData("http-error")]
    public async Task ResponseDisposal_PreservesBorrowedFactory(string kind)
    {
        var raw = JinaRerankFixtures.Read("text"); if (kind == "invalid") raw["results"]![0]!["index"] = 0;
        using var content = new TrackedRerankContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "http-error" ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = content }));
        using var factory = new BorrowedRerankFactory(handler); var adapter = new JinaRerankAdapter(factory, new() { Model = "actual", ApiKey = "key" }); var client = JinaRerankFixtures.Build(adapter);
        if (kind == "success") Assert.Equal(2, (await client.Rerank.RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken)).JinaRankings.Count);
        else if (kind == "invalid") await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<RerankUpstreamException>(() => client.Rerank.RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
        using var borrowed = factory.CreateClient("still-owned"); Assert.False(factory.Disposed);
    }
}
