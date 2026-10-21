using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Embedding.Jina.Tests;

/// <summary>验证原生错误、完整目标、取消、快照与借用资源，不联调真实供应商。</summary>
public sealed class JinaEmbeddingLifecycleTests
{
    /// <summary>HTTP 错误正文无需为成功 JSON，必须保留原始状态和正文且不重试。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(422)] [InlineData(429)] [InlineData(503)]
    public async Task HttpErrors_KeepOriginalStatusAndBody(int status)
    {
        const string body = "upstream failure\nraw response";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") }));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>Jina 原生 detail/code 与普通 error 即使 HTTP 200 且同时有 data 也必须失败并保留正文。</summary>
    [Theory] [InlineData("detail")] [InlineData("code")] [InlineData("error")]
    public async Task NativeBusinessError_WithDataCannotBecomeSuccess(string name)
    {
        var raw = JinaEmbeddingFixtures.Read(); raw[name] = name == "detail" ? new JsonObject { ["reason"] = "bad input" } : JsonValue.Create("INPUT_TOKEN_LIMIT_EXCEEDED");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaEmbeddingFixtures.Json(raw)));
        var error = await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(error.ResponseBody))); Assert.Equal(1, handler.Calls);
    }

    /// <summary>配置在构造时验证，不能在网络调用后才发现目标或身份不完整。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("null-root")] [InlineData("model")] [InlineData("key")] [InlineData("id")] [InlineData("name")]
    public void InvalidOptions_DoNotCreateClient(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new JinaEmbeddingTestFactory(handler);
        var root = kind switch
        {
            "relative" => new Uri("v1/", UriKind.Relative), "scheme" => new Uri("ftp://jina.invalid/v1/"), "userinfo" => new Uri("https://user:pass@jina.invalid/v1/"),
            "query" => new Uri("https://jina.invalid/v1/?key=x"), "fragment" => new Uri("https://jina.invalid/v1/#x"), "null-root" => null!, _ => new Uri("https://jina.invalid/v1/")
        };
        Assert.ThrowsAny<ArgumentException>(() => new JinaEmbeddingAdapter(factory, new()
        { Model = kind == "model" ? " " : "native", ApiKey = kind == "key" ? " " : "key", BaseAddress = root, Id = kind == "id" ? " " : "native", HttpClientName = kind == "name" ? " " : "native-jina" }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>空工厂或选项不能创建不完整适配器。</summary>
    [Fact]
    public void NullFactoryOrOptions_AreRejected()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentNullException>(() => new JinaEmbeddingAdapter(null!, new() { Model = "native" }));
        Assert.Throws<ArgumentNullException>(() => new JinaEmbeddingAdapter(new StubHttpClientFactory(handler), null!));
    }

    /// <summary>直接调用适配器时仍验证空请求、模型、参数集合及 Undefined。</summary>
    [Theory] [InlineData("request")] [InlineData("model")] [InlineData("parameters")] [InlineData("undefined")]
    public async Task InvalidDirectRequest_IsRejected(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var request = kind switch
        {
            "request" => null!, "model" => new EmbeddingRequest { Model = " " }, "parameters" => new EmbeddingRequest { Model = "public", Parameters = null! },
            _ => new EmbeddingRequest { Model = "public", Parameters = new() { ["input"] = default } }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>API 根路径有无尾斜线都保留前缀，模型不变，密钥只出现在请求头。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EndpointAndSecrets_AreNotRewritten(bool slash)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://jina.invalid/tenant/v2/embeddings", message.RequestUri!.AbsoluteUri); Assert.Equal("secret-jina-fixture", message.Headers.Authorization!.Parameter);
            var body = await message.Content!.ReadAsStringAsync(token); Assert.DoesNotContain("secret-jina-fixture", body);
            Assert.Equal("team/native:tag", JsonNode.Parse(body)!["model"]!.GetValue<string>()); return JinaEmbeddingFixtures.Json(JinaEmbeddingFixtures.Read());
        });
        using var factory = new JinaEmbeddingTestFactory(handler); var options = new JinaEmbeddingOptions
        { Model = "team/native:tag", ApiKey = "secret-jina-fixture", BaseAddress = new("https://jina.invalid/tenant/v2" + (slash ? "/" : "")), HttpClientName = "native-jina" };
        Assert.DoesNotContain("secret-jina-fixture", JsonSerializer.Serialize(options));
        var result = await new JinaEmbeddingAdapter(factory, options).EmbedAsync(JinaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("team/native:tag", result.Metadata.UpstreamModel); Assert.Equal("native-jina", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>预取消不创建客户端，发送与正文读取期间的取消都传递且不会重发。</summary>
    [Theory] [InlineData("before")] [InlineData("send")] [InlineData("body")]
    public async Task Cancellation_IsPropagatedWithoutRetry(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new JinaEmbeddingTestContent("{}", waitForCancellation: phase == "body");
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            sent.TrySetResult(); if (phase == "send") await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var factory = new JinaEmbeddingTestFactory(handler); var adapter = new JinaEmbeddingAdapter(factory, new() { Model = "native", ApiKey = "key" });
        if (phase == "before") cancellation.Cancel(); var call = adapter.EmbedAsync(JinaEmbeddingFixtures.Request(), cancellation.Token);
        if (phase != "before") await (phase == "send" ? sent.Task : content.Started.Task).WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(phase == "before" ? 0 : 1, handler.Calls); if (phase == "before") Assert.Empty(factory.Names);
        if (phase == "body") Assert.True(content.Disposed);
    }

    /// <summary>成功或失败都释放响应，SDK 不取得 HTTP 工厂与处理器的释放权。</summary>
    [Theory] [InlineData("success")] [InlineData("protocol")] [InlineData("upstream")]
    public async Task Disposal_PreservesBorrowedOwnership(string kind)
    {
        var raw = JinaEmbeddingFixtures.Read("multi"); if (kind == "protocol") raw.Remove("data");
        using var content = new JinaEmbeddingTestContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "upstream" ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = content }));
        using var factory = new JinaEmbeddingTestFactory(handler); var adapter = new JinaEmbeddingAdapter(factory, new() { Model = "native", ApiKey = "key" }); var client = JinaEmbeddingFixtures.Build(adapter);
        if (kind == "protocol") await Assert.ThrowsAsync<InvalidDataException>(() => client.Embedding.EmbedAsync(JinaEmbeddingFixtures.Request("multi"), TestContext.Current.CancellationToken));
        else if (kind == "upstream") await Assert.ThrowsAsync<EmbeddingUpstreamException>(() => client.Embedding.EmbedAsync(JinaEmbeddingFixtures.Request("multi"), TestContext.Current.CancellationToken));
        else
        {
            var result = await client.Embedding.EmbedAsync(JinaEmbeddingFixtures.Request("multi"), TestContext.Current.CancellationToken);
            Assert.True(content.Disposed); Assert.Equal("e", result.JinaEmbeddings[0].TokenizedInput![0]);
        }
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed);
        using var borrowed = factory.CreateClient("still-caller-owned"); Assert.Equal(1, handler.Calls);
    }

    /// <summary>流响应头在读正文前拒绝，不能将同步调用偷偷升级为流状态机。</summary>
    [Theory] [InlineData("text/event-stream")] [InlineData("application/x-ndjson")] [InlineData("application/ndjson")]
    public async Task StreamContentType_IsRejectedBeforeReading(string type)
    {
        using var content = new JinaEmbeddingTestContent(JinaEmbeddingFixtures.Read().ToJsonString()); content.Headers.ContentType = new(type);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.False(content.Read); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>直接调用和模块调用都在派发前快照复杂融合输入，不受原字典及 JSON 文档释放影响。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DispatchedInput_IsIndependent(bool viaModule)
    {
        using var document = JsonDocument.Parse(JinaEmbeddingFixtures.Read("group", "parameters")["input"]!.ToJsonString());
        var request = JinaEmbeddingFixtures.Request("group"); request.Parameters["input"] = document.RootElement;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            request.Parameters["input"] = JsonSerializer.SerializeToElement("changed"); document.Dispose();
            Assert.True(JsonNode.DeepEquals(JinaEmbeddingFixtures.Read("group", "wire"), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return JinaEmbeddingFixtures.Json(JinaEmbeddingFixtures.Read("group"));
        });
        var adapter = JinaEmbeddingFixtures.Adapter(handler); await using var client = JinaEmbeddingFixtures.Build(adapter);
        var result = viaModule ? await client.Embedding.EmbedAsync(request, TestContext.Current.CancellationToken) : await adapter.EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 0 }, result.JinaEmbeddings.Select(v => v.Index)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>数组、null 和布尔 JSON 不是原生成功响应。</summary>
    [Theory] [InlineData("[]")] [InlineData("null")] [InlineData("false")]
    public async Task NonObjectResponse_IsRejected(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaEmbeddingFixtures.Adapter(handler).EmbedAsync(JinaEmbeddingFixtures.Request(), TestContext.Current.CancellationToken));
    }
}
