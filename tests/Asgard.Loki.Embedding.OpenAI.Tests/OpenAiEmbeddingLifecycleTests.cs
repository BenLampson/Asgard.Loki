using System.Net;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>校验独立目标配置、取消和借用资源生命周期。</summary>
public sealed class OpenAiEmbeddingLifecycleTests
{
    /// <summary>意外 SSE 在响应头阶段失败，不等待或聚合可能永不结束的事件正文。</summary>
    [Fact]
    public async Task UnexpectedSse_IsRejectedWithoutReadingBody()
    {
        using var content = new TrackedEmbeddingContent("data: {}\n\n"); content.Headers.ContentType = new("text/event-stream");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAiEmbeddingFixtures.Adapter(handler).EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.False(content.Serialized); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>错误地址或缺失身份在创建任何 HTTP 客户端之前拒绝。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("id")] [InlineData("model")] [InlineData("key")] [InlineData("name")]
    public void InvalidConfiguration_FailsWithoutHttp(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedEmbeddingFactory(handler);
        var root = kind switch { "relative" => new Uri("v1/", UriKind.Relative), "scheme" => new Uri("ftp://embed.invalid/"),
            "userinfo" => new Uri("https://user:pass@embed.invalid/"), "query" => new Uri("https://embed.invalid/v1/?token=x"),
            "fragment" => new Uri("https://embed.invalid/v1/#x"), _ => new Uri("https://embed.invalid/v1/") };
        Assert.ThrowsAny<ArgumentException>(() => new OpenAiEmbeddingAdapter(factory, new()
        { Id = kind == "id" ? " " : "native", Model = kind == "model" ? " " : "actual", ApiKey = kind == "key" ? " " : "key",
            HttpClientName = kind == "name" ? " " : "native-http", BaseAddress = root }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>地址保留版本/租户路径，可选头部没有默认值，密钥不进入选项 JSON。</summary>
    [Theory] [InlineData("https://embed.invalid/custom/v2")] [InlineData("https://embed.invalid/custom/v2/")]
    public async Task EndpointAndOptionalHeaders_AreExact(string root)
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("https://embed.invalid/custom/v2/embeddings", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("OpenAI-Organization")); Assert.False(request.Headers.Contains("OpenAI-Project"));
            return Task.FromResult(OpenAiEmbeddingFixtures.Json(OpenAiEmbeddingFixtures.Read("response-2-float")));
        });
        using var factory = new BorrowedEmbeddingFactory(handler); var options = new OpenAiEmbeddingOptions
        { Model = "actual", ApiKey = "secret-fixture-key", BaseAddress = new(root), HttpClientName = "native-http" };
        Assert.DoesNotContain("secret-fixture-key", JsonSerializer.Serialize(options));
        var adapter = new OpenAiEmbeddingAdapter(factory, options);
        await adapter.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("native-http", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>预取消的直接适配器调用不能创建 HTTP 客户端。</summary>
    [Fact]
    public async Task PreCancellation_DoesNotCreateHttpClient()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new BorrowedEmbeddingFactory(handler);
        var adapter = new OpenAiEmbeddingAdapter(factory, new() { Model = "actual", ApiKey = "key" });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.EmbedAsync(OpenAiEmbeddingFixtures.Request(), cancellation.Token));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>调用中的取消到达所选 HTTP 请求，不增加第二次发送。</summary>
    [Fact]
    public async Task ActiveCancellation_ReachesHttpWithoutRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (_, token) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
        var adapter = OpenAiEmbeddingFixtures.Adapter(handler); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = adapter.EmbedAsync(OpenAiEmbeddingFixtures.Request(), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.Equal(1, handler.Calls);
    }

    /// <summary>HTTP 成功、协议错误、HTTP 错误均释放响应内容，模块释放后借用工厂和适配器仍能独立使用。</summary>
    [Theory] [InlineData("success")] [InlineData("invalid")] [InlineData("http-error")]
    public async Task ResponseDisposal_DoesNotDisposeBorrowedFactory(string kind)
    {
        var raw = OpenAiEmbeddingFixtures.Read("response-2-float"); if (kind == "invalid") raw["data"]![0]!["index"] = 0;
        using var content = new TrackedEmbeddingContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "http-error" ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = content }));
        using var factory = new BorrowedEmbeddingFactory(handler);
        var adapter = new OpenAiEmbeddingAdapter(factory, new() { Model = "actual", ApiKey = "key" }); var client = OpenAiEmbeddingFixtures.Build(adapter);
        if (kind == "success")
        {
            var result = await client.Embedding.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
            Assert.True(content.Disposed); Assert.Equal(1, result.OpenAiVectors[0].Index);
        }
        else if (kind == "invalid") await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => client.Embedding.EmbedAsync(OpenAiEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
        using var borrowedClient = factory.CreateClient("still-owned-by-caller");
        Assert.False(factory.Disposed);
    }
}
