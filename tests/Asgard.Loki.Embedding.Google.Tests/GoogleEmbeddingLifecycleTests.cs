using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Google.Tests;

/// <summary>验证两种同步操作的错误、取消、完整目标和借用资源生命周期。</summary>
public sealed class GoogleEmbeddingLifecycleTests
{
    /// <summary>HTTP 错误保留原始状态和文本，Retry-After 也不能触发 SDK 重试。</summary>
    [Theory] [InlineData(400, false)] [InlineData(400, true)] [InlineData(401, false)] [InlineData(401, true)]
    [InlineData(429, false)] [InlineData(429, true)] [InlineData(503, false)] [InlineData(503, true)]
    public async Task HttpFailure_PreservesBodyWithoutRetry(int status, bool batch)
    {
        const string body = "\n upstream unavailable <raw> \n";
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            response.Headers.Add("Retry-After", "1"); return Task.FromResult(response);
        });
        var name = batch ? "batch-config" : "single-config";
        await using var client = GoogleEmbeddingFixtures.Build(GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingFixtures.Operation(name)));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => client.Embedding.EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生 200 业务错误也不能转换为嵌入成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BusinessError_PreservesActualStatus(bool batch)
    {
        const string body = "\n{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"future\":false}}\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var name = batch ? "batch-config" : "single-config";
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingFixtures.Operation(name))
            .EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>截断、非法 JSON 和重复字段显式失败，不保留部分成功向量。</summary>
    [Theory] [InlineData("invalid", false)] [InlineData("invalid", true)] [InlineData("truncated", false)] [InlineData("truncated", true)]
    [InlineData("duplicate", false)] [InlineData("duplicate", true)]
    public async Task InvalidJson_CannotBecomeSuccess(string kind, bool batch)
    {
        var body = kind switch { "invalid" => "not-json", "truncated" => "{\"embedding\":", _ => "{\"embedding\":{},\"embedding\":{}}" };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var name = batch ? "batch-config" : "single-config";
        await Assert.ThrowsAnyAsync<JsonException>(() => GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingFixtures.Operation(name))
            .EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>误返回 SSE 时只检查响应头，不等待无限事件正文并正确释放内容。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Sse_IsRejectedAtHeaders(bool batch)
    {
        using var content = new GoogleEmbeddingTestContent("data: {}\n\n"); content.Headers.ContentType = new("text/event-stream");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var name = batch ? "batch-config" : "single-config";
        await Assert.ThrowsAsync<InvalidDataException>(() => GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingFixtures.Operation(name))
            .EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken));
        Assert.False(content.Read); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>无效基础地址、模型路径或操作枚举必须在构造时拒绝。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("prefix")] [InlineData("model-path")] [InlineData("model-space")] [InlineData("key")] [InlineData("name")]
    [InlineData("id")] [InlineData("operation")]
    public void InvalidOptions_CannotCreateClient(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new GoogleEmbeddingTestFactory(handler);
        var root = kind switch { "relative" => new Uri("v1beta/", UriKind.Relative), "scheme" => new Uri("ftp://google.invalid/"),
            "userinfo" => new Uri("https://user:pass@google.invalid/"), "query" => new Uri("https://google.invalid/v1beta/?key=x"),
            "fragment" => new Uri("https://google.invalid/v1beta/#x"), _ => new Uri("https://google.invalid/v1beta/") };
        var model = kind switch { "prefix" => "models/embedding-test", "model-path" => "other/model", "model-space" => "embedding test", _ => "embedding-test" };
        Assert.ThrowsAny<ArgumentException>(() => new GoogleEmbeddingAdapter(factory, new() { Model = model, BaseAddress = root,
            ApiKey = kind == "key" ? " " : "key", HttpClientName = kind == "name" ? " " : "google-native", Id = kind == "id" ? " " : "native",
            Operation = kind == "operation" ? (GoogleEmbeddingOperation)99 : GoogleEmbeddingOperation.EmbedContent }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>保留租户/版本根路径；密钥仅放请求头且不会序列化到选项中。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task EndpointAndSecrets_StayInTheirNativePositions(bool slash, bool batch)
    {
        var name = batch ? "batch-config" : "single-config"; var operation = GoogleEmbeddingFixtures.Operation(name);
        var root = "https://google.invalid/custom/v2" + (slash ? "/" : "");
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("https://google.invalid/custom/v2/models/embedding-test:" + (batch ? "batchEmbedContents" : "embedContent"), message.RequestUri!.AbsoluteUri);
            Assert.Equal("secret-google-fixture", Assert.Single(message.Headers.GetValues("x-goog-api-key")));
            Assert.Equal("", message.RequestUri.Query); return Task.FromResult(GoogleEmbeddingFixtures.Json(GoogleEmbeddingFixtures.Read(name + "-response")));
        });
        using var factory = new GoogleEmbeddingTestFactory(handler); var options = new GoogleEmbeddingOptions
        { Model = "embedding-test", ApiKey = "secret-google-fixture", BaseAddress = new(root), Operation = operation, HttpClientName = "google-native" };
        Assert.DoesNotContain("secret-google-fixture", JsonSerializer.Serialize(options));
        await new GoogleEmbeddingAdapter(factory, options).EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken);
        Assert.Equal("google-native", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>预取消不创建 HTTP 客户端。</summary>
    [Fact]
    public async Task PreCancellation_DoesNotCreateClient()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new GoogleEmbeddingTestFactory(handler);
        var adapter = new GoogleEmbeddingAdapter(factory, new() { Model = "embedding-test", ApiKey = "key" });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.EmbedAsync(GoogleEmbeddingFixtures.Request(), cancellation.Token));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>请求中的取消到达 HTTP，批请求也不改发或自动拆分。</summary>
    [Fact]
    public async Task ActiveCancellation_DoesNotRetryBatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = GoogleEmbeddingFixtures.Adapter(handler, GoogleEmbeddingOperation.BatchEmbedContents).EmbedAsync(GoogleEmbeddingFixtures.Request("batch-config"), cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.Equal(1, handler.Calls);
    }

    /// <summary>成功/协议失败都释放响应；客户端不取得工厂释放权，结果快照在响应释放后仍可访问。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Disposal_KeepsBorrowedOwnership(bool invalid, bool batch)
    {
        var name = batch ? "batch-config" : "single-config"; var raw = GoogleEmbeddingFixtures.Read(name + "-response");
        if (invalid) raw.Remove(batch ? "embeddings" : "embedding");
        using var content = new GoogleEmbeddingTestContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var factory = new GoogleEmbeddingTestFactory(handler); var adapter = new GoogleEmbeddingAdapter(factory, new()
        { Model = "embedding-test", ApiKey = "key", Operation = GoogleEmbeddingFixtures.Operation(name) });
        var client = GoogleEmbeddingFixtures.Build(adapter);
        if (invalid) await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken));
        else
        {
            var result = await client.Embedding.EmbedAsync(GoogleEmbeddingFixtures.Request(name), TestContext.Current.CancellationToken);
            Assert.True(content.Disposed); Assert.Equal(new double[] { 0.1, 0.2 }, result.GoogleEmbeddings[0].Values);
        }
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed);
        using var borrowed = factory.CreateClient("caller-still-owns-factory"); Assert.Equal(1, handler.Calls);
    }
}
