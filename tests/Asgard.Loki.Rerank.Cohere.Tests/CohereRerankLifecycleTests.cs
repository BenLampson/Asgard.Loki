using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Cohere.Tests;

/// <summary>固定完整目标、直接调用快照、取消及借用工厂和响应的生命周期回归。</summary>
public sealed class CohereRerankLifecycleTests
{
    /// <summary>无效地址、身份和版本在任何 HTTP 客户端创建之前失败。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")]
    [InlineData("fragment")] [InlineData("null-root")] [InlineData("id")] [InlineData("model")]
    [InlineData("key")] [InlineData("name")] [InlineData("version-zero")] [InlineData("version-unknown")]
    public void InvalidConfiguration_FailsWithoutHttp(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedRerankFactory(handler);
        var root = kind switch { "relative" => new Uri("path/", UriKind.Relative), "scheme" => new Uri("ftp://rank.invalid/"),
            "userinfo" => new Uri("https://user:pass@rank.invalid/"), "query" => new Uri("https://rank.invalid/?key=x"),
            "fragment" => new Uri("https://rank.invalid/#x"), "null-root" => null!, _ => new Uri("https://rank.invalid/") };
        Assert.ThrowsAny<ArgumentException>(() => new CohereRerankAdapter(factory, new()
        {
            Id = kind == "id" ? " " : "id", Model = kind == "model" ? " " : "actual", ApiKey = kind == "key" ? " " : "key",
            HttpClientName = kind == "name" ? " " : "http", BaseAddress = root,
            ApiVersion = kind == "version-zero" ? 0 : kind == "version-unknown" ? (CohereRerankApiVersion)3 : CohereRerankApiVersion.V2
        }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>服务根保留完整租户路径，版本只由配置选择，可选头没有默认值且密钥不序列化。</summary>
    [Theory] [InlineData("https://rank.invalid/proxy", CohereRerankApiVersion.V1)]
    [InlineData("https://rank.invalid/proxy/", CohereRerankApiVersion.V2)]
    public async Task EndpointHeadersAndCredentials_AreExplicit(string root, CohereRerankApiVersion version)
    {
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal($"https://rank.invalid/proxy/v{(int)version}/rerank", message.RequestUri!.AbsoluteUri);
            Assert.False(message.Headers.Contains("X-Client-Name")); Assert.Equal("key-private", message.Headers.Authorization!.Parameter);
            return Task.FromResult(CohereRerankFixtures.Json(CohereRerankFixtures.Read("v2-basic")));
        });
        using var factory = new BorrowedRerankFactory(handler);
        var options = new CohereRerankOptions { Model = "actual", ApiVersion = version, ApiKey = "key-private", BaseAddress = new(root), HttpClientName = "native-http" };
        Assert.DoesNotContain("key-private", JsonSerializer.Serialize(options));
        await new CohereRerankAdapter(factory, options).RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("native-http", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>直接适配器调用也独立保存原生文档及未知字段，外部 JSON 释放后仍正确验证原文档关联。</summary>
    [Fact]
    public async Task DirectCall_SnapshotsRequestBeforeAwait()
    {
        using var source = JsonDocument.Parse("[\"A😀\",\"B\"]");
        var request = CohereRerankFixtures.Request("v1-text"); request.Parameters["documents"] = source.RootElement;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token);
            Assert.True(JsonNode.DeepEquals(CohereRerankFixtures.Read("v1-text", "wire"), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return CohereRerankFixtures.Json(CohereRerankFixtures.Read("v1-text"));
        });
        var call = CohereRerankFixtures.Adapter(handler, CohereRerankApiVersion.V1).RerankAsync(request, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); source.Dispose(); request.Parameters.Clear(); release.SetResult();
        var result = await call; Assert.Equal("B", result.CohereRankings[0].Document!.Value.GetProperty("text").GetString());
    }

    /// <summary>预取消不会创建客户端或选择备用目标。</summary>
    [Fact]
    public async Task PreCancelledCall_DoesNotCreateClient()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedRerankFactory(handler);
        var adapter = new CohereRerankAdapter(factory, new() { Model = "actual", ApiKey = "key", ApiVersion = CohereRerankApiVersion.V2 });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RerankAsync(CohereRerankFixtures.Request(), cancellation.Token));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>HTTP 发送中的取消直接传递，不重发或切换 API 版本。</summary>
    [Fact]
    public async Task ActiveCancellation_ReachesHttp()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.Equal(1, handler.Calls);
    }

    /// <summary>读取正文时取消必须停止读取并释放响应，不包装为有效完整排名。</summary>
    [Fact]
    public async Task BodyCancellation_DisposesResponse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new TrackedRerankContent("", async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>即使正文实现不理会取消并写出有效 JSON，完成读取后也不能返回成功。</summary>
    [Fact]
    public async Task CancellationAfterBodyWrite_IsNotSuccessfulResult()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new TrackedRerankContent("", async (stream, _) =>
        { await stream.WriteAsync(Encoding.UTF8.GetBytes(CohereRerankFixtures.Read("v2-basic").ToJsonString()), TestContext.Current.CancellationToken); cancellation.Cancel(); });
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), cancellation.Token));
        Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>成功、协议失败和 HTTP 失败都释放本次响应；模块不取得借用工厂与适配器所有权。</summary>
    [Theory] [InlineData("success")] [InlineData("invalid")] [InlineData("http-error")]
    public async Task ResponseDisposal_DoesNotDisposeBorrowedFactory(string kind)
    {
        var raw = CohereRerankFixtures.Read("v2-basic"); if (kind == "invalid") raw["results"]![0]!["index"] = 0;
        using var content = new TrackedRerankContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "http-error" ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = content }));
        using var factory = new BorrowedRerankFactory(handler);
        var adapter = new CohereRerankAdapter(factory, new() { Model = "actual", ApiKey = "key", ApiVersion = CohereRerankApiVersion.V2 });
        var client = CohereRerankFixtures.Build(adapter);
        if (kind == "success") Assert.Equal(2, (await client.Rerank.RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken)).CohereRankings.Count);
        else if (kind == "invalid") await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<RerankUpstreamException>(() => client.Rerank.RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
        using var borrowed = factory.CreateClient("still-borrowed"); Assert.False(factory.Disposed);
    }
}
