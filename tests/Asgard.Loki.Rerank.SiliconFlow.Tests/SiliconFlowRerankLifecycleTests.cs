using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>原生 HTTP、错误、取消和释放路径不依赖真实凭据、服务或自动重试。</summary>
public sealed class SiliconFlowRerankLifecycleTests
{
    /// <summary>HTTP 错误保留原始状态及 Unicode 正文，不折叠为正常排名或重试。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)] [InlineData(500)] [InlineData(503)] [InlineData(504)]
    public async Task HttpError_PreservesStatusAndBody(int status)
    {
        const string body = "{\"code\":20012,\"message\":\"native error 首😀\",\"data\":null}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生业务错误即使附带结果也不是成功；纯错误的 message/detail 外壳不变成空数组。</summary>
    [Theory] [InlineData("code")] [InlineData("error")] [InlineData("message")] [InlineData("detail")]
    public async Task BusinessError_CannotHideBehindResults(string field)
    {
        var raw = SiliconFlowRerankFixtures.Read();
        if (field is "message" or "detail") raw.Remove("results");
        raw[field] = field == "code" ? JsonValue.Create(50505) : JsonValue.Create("native failure"); var body = raw.ToJsonString();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(raw)));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>成功 SSE/NDJSON/文本媒体类型在正文读取前拒绝，不能把流的第一项当作完整响应。</summary>
    [Theory] [InlineData("text/event-stream")] [InlineData("application/x-ndjson")] [InlineData("text/plain")]
    public async Task UnsupportedMedia_IsRejectedAtHeaders(string media)
    {
        using var content = new SiliconFlowRerankTestContent(SiliconFlowRerankFixtures.Read().ToJsonString()); content.Headers.ContentType = new(media);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, content.ReadCalls); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>损坏、截断、重复属性或多个 JSON 文档都不能只解析出第一份成功。</summary>
    [Theory] [InlineData("{")] [InlineData("{\"id\":\"one\",\"id\":\"two\",\"results\":[]}")] [InlineData("{}{}")] [InlineData("data: {}\n\n")]
    public async Task MalformedJson_IsNotRepaired(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 UTF-8 非法字节不能经替代字符成为另一份看似正常的 JSON。</summary>
    [Theory] [InlineData("FF")] [InlineData("C080")]
    public async Task InvalidUtf8_IsNotReplaced(string hex)
    {
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromHexString(hex)) }; response.Content.Headers.ContentType = new("application/json"); return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => SiliconFlowRerankFixtures.Adapter(handler).RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>预取消、发送/正文等待、正文结束及资源释放阶段都不交付取消后的成功结果。</summary>
    [Theory] [InlineData("before")] [InlineData("send")] [InlineData("body")] [InlineData("eof")] [InlineData("release")] [InlineData("client-release")]
    public async Task Cancellation_PropagatesThroughFinalDisposal(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var content = new SiliconFlowRerankTestContent(SiliconFlowRerankFixtures.Read().ToJsonString())
        { WaitForCancellation = phase == "body", AfterBody = phase == "eof" ? cancellation.Cancel : null, OnDispose = phase == "release" ? cancellation.Cancel : null };
        content.Headers.ContentType = new("application/json");
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            sent.TrySetResult(); if (phase == "send") await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var factory = new SiliconFlowRerankTestFactory(handler) { OnClientDispose = phase == "client-release" ? cancellation.Cancel : null };
        if (phase == "before") cancellation.Cancel();
        var operation = new SiliconFlowRerankAdapter(factory, SiliconFlowRerankFixtures.Options()).RerankAsync(SiliconFlowRerankFixtures.Request(), cancellation.Token);
        if (phase == "send") { await sent.Task; cancellation.Cancel(); }
        if (phase == "body") { await content.Started.Task; cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(phase == "before" ? 0 : 1, handler.Calls);
        Assert.Equal(phase == "before" ? 0 : 1, factory.ClientDisposals); Assert.False(factory.Disposed);
        if (phase is not ("before" or "send")) Assert.True(content.Disposed);
    }
    /// <summary>完整 JSON 后读取或释放错误也必须失败，响应与请求级客户端均退出作用域。</summary>
    [Theory] [InlineData("body")] [InlineData("response")] [InlineData("client")]
    public async Task TailOrDisposalFailure_DoesNotReturnSuccess(string phase)
    {
        using var content = new SiliconFlowRerankTestContent(SiliconFlowRerankFixtures.Read().ToJsonString())
        { ThrowAfterBody = phase == "body", OnDispose = phase == "response" ? () => throw new IOException("response release failed") : null };
        content.Headers.ContentType = new("application/json");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var factory = new SiliconFlowRerankTestFactory(handler) { OnClientDispose = phase == "client" ? () => throw new IOException("client release failed") : null };
        var adapter = new SiliconFlowRerankAdapter(factory, SiliconFlowRerankFixtures.Options());
        if (phase == "body")
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => adapter.RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken));
            Assert.IsType<IOException>(error.InnerException);
        }
        else await Assert.ThrowsAsync<IOException>(() => adapter.RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.True(content.Disposed); Assert.Equal(1, factory.ClientDisposals); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>SDK 核心及供应商仅释放创建的客户端，不释放调用方工厂或借用的处理器。</summary>
    [Fact]
    public async Task PublicSdkDisposal_DoesNotOwnFactoryOrHandler()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SiliconFlowRerankFixtures.Json(SiliconFlowRerankFixtures.Read())));
        using var factory = new SiliconFlowRerankTestFactory(handler); var adapter = new SiliconFlowRerankAdapter(factory, SiliconFlowRerankFixtures.Options());
        await using var client = SiliconFlowRerankFixtures.Build(adapter); await client.Rerank.RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken);
        await client.DisposeAsync(); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientDisposals);
        await adapter.RerankAsync(SiliconFlowRerankFixtures.Request(), TestContext.Current.CancellationToken); Assert.Equal(2, handler.Calls); Assert.Equal(2, factory.ClientDisposals);
    }
}
