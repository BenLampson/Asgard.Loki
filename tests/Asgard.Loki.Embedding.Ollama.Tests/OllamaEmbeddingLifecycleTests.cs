using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Ollama.Tests;

/// <summary>验证完整目标、取消、借用生命周期与零输入的严格响应关联。</summary>
public sealed class OllamaEmbeddingLifecycleTests
{
    /// <summary>无效目标在构造时失败，不读取环境、发请求或尝试其他目标。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("null-root")] [InlineData("model")] [InlineData("key")] [InlineData("id")] [InlineData("name")]
    public void InvalidOptions_DoNotCreateClient(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new OllamaEmbeddingTestFactory(handler);
        var root = kind switch
        {
            "relative" => new Uri("api/", UriKind.Relative), "scheme" => new Uri("ftp://ollama.invalid/api/"),
            "userinfo" => new Uri("https://user:pass@ollama.invalid/api/"), "query" => new Uri("https://ollama.invalid/api/?key=x"),
            "fragment" => new Uri("https://ollama.invalid/api/#x"), "null-root" => null!, _ => new Uri("https://ollama.invalid/api/")
        };
        Assert.ThrowsAny<ArgumentException>(() => new OllamaEmbeddingAdapter(factory, new()
        {
            Model = kind == "model" ? " " : "embedding-test", BaseAddress = root, ApiKey = kind == "key" ? " " : null,
            Id = kind == "id" ? " " : "ollama", HttpClientName = kind == "name" ? " " : "ollama-native"
        }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>空工厂或选项立即失败。</summary>
    [Fact]
    public void NullOptionsOrFactory_AreRejected()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentNullException>(() => new OllamaEmbeddingAdapter(null!, new() { Model = "test" }));
        Assert.Throws<ArgumentNullException>(() => new OllamaEmbeddingAdapter(new StubHttpClientFactory(handler), null!));
    }

    /// <summary>根路径有无尾斜线都保留租户前缀；认证仅在显式配置时发送，不进入正文或选项序列化。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task EndpointAndAuthentication_PreserveExplicitConfiguration(bool slash, bool authenticated)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://ollama.invalid/prefix/api/embed", message.RequestUri!.AbsoluteUri);
            if (authenticated) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("secret-fixture", message.Headers.Authorization.Parameter); }
            else Assert.Null(message.Headers.Authorization);
            Assert.DoesNotContain("secret-fixture", await message.Content!.ReadAsStringAsync(token));
            return OllamaEmbeddingFixtures.Json(OllamaEmbeddingFixtures.Read());
        });
        using var factory = new OllamaEmbeddingTestFactory(handler); var options = new OllamaEmbeddingOptions
        { Model = "team/embed-test:tag", BaseAddress = new("https://ollama.invalid/prefix/api" + (slash ? "/" : "")), ApiKey = authenticated ? "secret-fixture" : null, HttpClientName = "native-ollama" };
        Assert.DoesNotContain("secret-fixture", JsonSerializer.Serialize(options));
        await new OllamaEmbeddingAdapter(factory, options).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("native-ollama", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>默认目标不需要 Key，模型完整字符串不因路径分隔符或 digest 被 SDK 改写。</summary>
    [Fact]
    public async Task DefaultRootAndDigest_AreNotRewritten()
    {
        const string model = "registry.invalid/team/embed@sha256:0123";
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("http://localhost:11434/api/embed", message.RequestUri!.AbsoluteUri); Assert.Null(message.Headers.Authorization);
            Assert.Equal(model, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!["model"]!.GetValue<string>());
            return OllamaEmbeddingFixtures.Json(OllamaEmbeddingFixtures.Read());
        });
        await new OllamaEmbeddingAdapter(new StubHttpClientFactory(handler), new() { Model = model }).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>预取消不创建客户端，请求中及正文中的取消都传到原始 HTTP 且不重发。</summary>
    [Theory] [InlineData("before")] [InlineData("send")] [InlineData("body")]
    public async Task Cancellation_IsPropagatedWithoutRetry(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new OllamaEmbeddingTestContent("{}", waitForCancellation: phase == "body");
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            sent.TrySetResult();
            if (phase == "send") await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var factory = new OllamaEmbeddingTestFactory(handler); var adapter = new OllamaEmbeddingAdapter(factory, new() { Model = "test" });
        if (phase == "before") cancellation.Cancel();
        var call = adapter.EmbedAsync(OllamaEmbeddingFixtures.Request(), cancellation.Token);
        if (phase != "before") await (phase == "send" ? sent.Task : content.Started.Task).WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(phase == "before" ? 0 : 1, handler.Calls);
        if (phase == "before") Assert.Empty(factory.Names);
        if (phase == "body") Assert.True(content.Disposed);
    }

    /// <summary>成功、协议错误和业务错误均释放响应；模块释放不取得调用者工厂所有权。</summary>
    [Theory] [InlineData("success")] [InlineData("protocol")] [InlineData("upstream")]
    public async Task Disposal_PreservesBorrowedOwnership(string kind)
    {
        var raw = OllamaEmbeddingFixtures.Read(); if (kind == "protocol") raw.Remove("embeddings");
        using var content = new OllamaEmbeddingTestContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "upstream" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = content }));
        using var factory = new OllamaEmbeddingTestFactory(handler); var adapter = new OllamaEmbeddingAdapter(factory, new() { Model = "test" });
        var client = OllamaEmbeddingFixtures.Build(adapter);
        if (kind == "protocol") await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        else if (kind == "upstream") await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => client.Embedding.EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        else
        {
            var result = await client.Embedding.EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
            Assert.True(content.Disposed); Assert.Equal(0.1, result.OllamaEmbeddings[0].Values[0]);
        }
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed);
        using var borrowed = factory.CreateClient("still-caller-owned"); Assert.Equal(1, handler.Calls);
    }

    /// <summary>SSE 与 NDJSON 头在读正文前拒绝，单对象流也不能冒充同步成功响应。</summary>
    [Theory] [InlineData("text/event-stream")] [InlineData("application/x-ndjson")] [InlineData("application/ndjson")]
    public async Task StreamContentType_IsRejectedBeforeReading(string type)
    {
        using var content = new OllamaEmbeddingTestContent(OllamaEmbeddingFixtures.Read().ToJsonString()); content.Headers.ContentType = new(type);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.False(content.Read); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>显式零输入只能对应零向量，正常输入不能被上游悄悄当作加载调用。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EmptyAndNonemptyInput_CannotExchangeResponseCounts(bool emptyInput)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(OllamaEmbeddingFixtures.Json(OllamaEmbeddingFixtures.Read(emptyInput ? "single" : "empty"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaEmbeddingFixtures.Adapter(handler).EmbedAsync(OllamaEmbeddingFixtures.Request(emptyInput ? "empty" : "single"), TestContext.Current.CancellationToken));
    }

    /// <summary>派发前获取独立参数快照，随后修改原字典或释放 JSON 文档不影响最终正文。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DispatchedParameters_AreIndependentSnapshots(bool viaModule)
    {
        using var document = JsonDocument.Parse("[\"first\",\"second\"]");
        var request = OllamaEmbeddingFixtures.Request("batch"); request.Parameters["input"] = document.RootElement;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            request.Parameters["input"] = JsonSerializer.SerializeToElement("changed"); document.Dispose();
            Assert.True(JsonNode.DeepEquals(OllamaEmbeddingFixtures.Read("batch", "wire"), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return OllamaEmbeddingFixtures.Json(OllamaEmbeddingFixtures.Read("batch"));
        });
        var adapter = OllamaEmbeddingFixtures.Adapter(handler); await using var client = OllamaEmbeddingFixtures.Build(adapter);
        var result = viaModule ? await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken) : await adapter.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.OllamaEmbeddings.Count); Assert.Equal(1, handler.Calls);
    }
}
