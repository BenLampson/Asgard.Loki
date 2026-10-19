using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Ali.Tests;

/// <summary>验证原生错误、完整目标、取消、资源释放和请求独立性，不调用真实模型。</summary>
public sealed class AliRerankLifecycleTests
{
    /// <summary>HTTP 与业务错误保留原始状态和正文，已有 results 不能掩盖错误。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)] [InlineData(200)]
    public async Task UpstreamError_KeepsOriginalStatusAndBody(int status)
    {
        const string body = "{\"request_id\":\"r-error\",\"code\":\"InvalidApiKey\",\"message\":\"Invalid API-key provided.\"}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>200 内的 code、message 或 error 不会因同时存在合法排名而被忽略。</summary>
    [Theory] [InlineData("code")] [InlineData("message")] [InlineData("error")]
    public async Task BusinessErrorWithResults_DoesNotBecomeSuccess(string field)
    {
        var raw = AliRerankFixtures.Read(); raw[field] = "upstream failure";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AliRerankFixtures.Json(raw)));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(error.ResponseBody))); Assert.Equal(1, handler.Calls);
    }

    /// <summary>无效完整目标在构造时失败，不猜测工作空间、地域或其他能力凭据。</summary>
    [Theory] [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    [InlineData("null-root")] [InlineData("model")] [InlineData("key")] [InlineData("id")] [InlineData("name")]
    public void InvalidOptions_DoNotCreateClient(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new AliRerankTestFactory(handler);
        var root = kind switch
        {
            "relative" => new Uri("api/v1/", UriKind.Relative), "scheme" => new Uri("ftp://ali.invalid/api/v1/"),
            "userinfo" => new Uri("https://user:pass@ali.invalid/api/v1/"), "query" => new Uri("https://ali.invalid/api/v1/?key=x"),
            "fragment" => new Uri("https://ali.invalid/api/v1/#x"), "null-root" => null!, _ => new Uri("https://ali.invalid/api/v1/")
        };
        Assert.ThrowsAny<ArgumentException>(() => new AliRerankAdapter(factory, new()
        { BaseAddress = root, Model = kind == "model" ? " " : "native", ApiKey = kind == "key" ? " " : "key", Id = kind == "id" ? " " : "native", HttpClientName = kind == "name" ? " " : "ali-native" }));
        Assert.Empty(factory.Names); Assert.Equal(0, handler.Calls);
    }

    /// <summary>缺失工厂或配置不允许延迟到 HTTP 调用失败。</summary>
    [Fact]
    public void NullFactoryOrOptions_AreRejected()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentNullException>(() => new AliRerankAdapter(null!, new() { Model = "test", BaseAddress = new("https://ali.invalid/") }));
        Assert.Throws<ArgumentNullException>(() => new AliRerankAdapter(new StubHttpClientFactory(handler), null!));
    }

    /// <summary>直接调用时也拒绝空请求、空模型、空参数集合或 Undefined JSON。</summary>
    [Theory] [InlineData("request")] [InlineData("model")] [InlineData("parameters")] [InlineData("undefined")]
    public async Task InvalidDirectRequest_IsRejected(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var request = kind switch
        {
            "request" => null!, "model" => new RerankRequest { Model = " " }, "parameters" => new RerankRequest { Model = "public", Parameters = null! },
            _ => new RerankRequest { Model = "public", Parameters = new() { ["input"] = default } }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>根路径有无斜线都保留租户前缀，Key 只放认证头且不序列化。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EndpointAndSecrets_AreExplicit(bool slash)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://ali.invalid/tenant/api/v2/services/rerank/text-rerank/text-rerank", message.RequestUri!.AbsoluteUri);
            Assert.Equal("secret-ali-fixture", message.Headers.Authorization!.Parameter); Assert.Equal("", message.RequestUri.Query);
            Assert.DoesNotContain("secret-ali-fixture", await message.Content!.ReadAsStringAsync(token)); return AliRerankFixtures.Json(AliRerankFixtures.Read());
        });
        using var factory = new AliRerankTestFactory(handler); var options = new AliRerankOptions
        { Model = "rerank-native", BaseAddress = new("https://ali.invalid/tenant/api/v2" + (slash ? "/" : "")), ApiKey = "secret-ali-fixture", HttpClientName = "ali-native" };
        Assert.DoesNotContain("secret-ali-fixture", JsonSerializer.Serialize(options));
        await new AliRerankAdapter(factory, options).RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("ali-native", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }

    /// <summary>预取消不发请求，发送和正文读取期间的取消到达替代 HTTP，均不重试。</summary>
    [Theory] [InlineData("before")] [InlineData("send")] [InlineData("body")]
    public async Task Cancellation_IsPropagatedWithoutRetry(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new AliRerankTestContent("{}", waitForCancellation: phase == "body");
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            sent.TrySetResult(); if (phase == "send") await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var factory = new AliRerankTestFactory(handler); var adapter = new AliRerankAdapter(factory, new() { Model = "native", ApiKey = "test", BaseAddress = new("https://ali.invalid/") });
        if (phase == "before") cancellation.Cancel(); var call = adapter.RerankAsync(AliRerankFixtures.Request(), cancellation.Token);
        if (phase != "before") await (phase == "send" ? sent.Task : content.Started.Task).WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(phase == "before" ? 0 : 1, handler.Calls);
        if (phase == "before") Assert.Empty(factory.Names);
        if (phase == "body") Assert.True(content.Disposed);
    }

    /// <summary>成功、协议错误和上游错误都释放响应，客户端释放不接管工厂或处理器。</summary>
    [Theory] [InlineData("success")] [InlineData("protocol")] [InlineData("upstream")]
    public async Task Disposal_PreservesBorrowedOwnership(string kind)
    {
        var raw = AliRerankFixtures.Read(); if (kind == "protocol") raw.Remove("output");
        using var content = new AliRerankTestContent(raw.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "upstream" ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = content }));
        using var factory = new AliRerankTestFactory(handler); var adapter = new AliRerankAdapter(factory, new() { Model = "native", ApiKey = "test", BaseAddress = new("https://ali.invalid/") });
        var client = AliRerankFixtures.Build(adapter);
        if (kind == "protocol") await Assert.ThrowsAsync<InvalidDataException>(() => client.Rerank.RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
        else if (kind == "upstream") await Assert.ThrowsAsync<RerankUpstreamException>(() => client.Rerank.RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
        else
        {
            var result = await client.Rerank.RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken);
            Assert.True(content.Disposed); Assert.Equal(2, result.AliRankings[0].Index);
        }
        await client.DisposeAsync(); Assert.True(content.Disposed); Assert.False(factory.Disposed);
        using var borrowed = factory.CreateClient("caller-still-owns-factory"); Assert.Equal(1, handler.Calls);
    }

    /// <summary>同步重排拒绝流响应头，不读取正文后再伪装终态。</summary>
    [Theory] [InlineData("text/event-stream")] [InlineData("application/x-ndjson")] [InlineData("application/ndjson")]
    public async Task StreamContentType_IsRejectedBeforeReading(string type)
    {
        using var content = new AliRerankTestContent(AliRerankFixtures.Read().ToJsonString()); content.Headers.ContentType = new(type);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => AliRerankFixtures.Adapter(handler).RerankAsync(AliRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.False(content.Read); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>公开模块与直接适配器都在派发前快照嵌套 input/parameters，不依赖外部文档生命周期。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestSnapshot_IsIndependent(bool viaModule)
    {
        using var document = JsonDocument.Parse(AliRerankFixtures.Read("top", "parameters")["input"]!.ToJsonString());
        var request = AliRerankFixtures.Request("top"); request.Parameters["input"] = document.RootElement;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            request.Parameters["input"] = JsonSerializer.SerializeToElement(new { query = "changed" }); document.Dispose();
            Assert.True(JsonNode.DeepEquals(AliRerankFixtures.Read("top", "wire"), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
            return AliRerankFixtures.Json(AliRerankFixtures.Read("top"));
        });
        var adapter = AliRerankFixtures.Adapter(handler); await using var client = AliRerankFixtures.Build(adapter);
        var result = viaModule ? await client.Rerank.RerankAsync(request, TestContext.Current.CancellationToken) : await adapter.RerankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2, 0 }, result.AliRankings.Select(r => r.Index)); Assert.Equal(1, handler.Calls);
    }
}
