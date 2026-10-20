using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.DeepSeek.Tests;

/// <summary>固定服务/认证配置和借用资源边界，调用者没有隐式凭据或目标回退。</summary>
public sealed class ToolsConfigurationTests
{
    /// <summary>非法固定根地址在构造时失败，不等发起原生请求。</summary>
    [Theory] [InlineData("relative")] [InlineData("ftp://tools.invalid/")] [InlineData("https://user:secret@tools.invalid/")]
    [InlineData("https://tools.invalid/?api-key=secret")] [InlineData("https://tools.invalid/#frag")]
    public void InvalidFixedRootCannotConstruct(string value)
    {
        using var factory = ToolsFixture.Factory(); var options = new DeepSeekToolsOptions { BaseAddress = new(value, UriKind.RelativeOrAbsolute), ApiKey = ToolsFixture.Key };
        Assert.Throws<ArgumentException>(() => new DeepSeekToolsClient(factory, options)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>缺失/无效凭据不借用工厂默认身份，且错误信息不回显凭据。</summary>
    [Theory] [InlineData("")] [InlineData(" ")] [InlineData("fixture key")] [InlineData("fixture\nkey")] [InlineData("凭据")]
    public void ExplicitCredentialMustBeValid(string key)
    {
        using var factory = ToolsFixture.Factory(); factory.ConfigureClient = client => client.DefaultRequestHeaders.Authorization = new("Bearer", "borrowed-other-key");
        var error = Assert.ThrowsAny<ArgumentException>(() => new DeepSeekToolsClient(factory, ToolsFixture.Options(key: key))); Assert.DoesNotContain("borrowed-other-key", error.Message); Assert.Equal(0, factory.Creates);
    }
    /// <summary>所有可配置字节上限必须为正，客户端名称也不能为空。</summary>
    [Theory] [InlineData("request", 0)] [InlineData("request", -1)] [InlineData("response", 0)] [InlineData("response", -1)]
    public void NonpositiveLimitsFailBeforeFactory(string kind, int value)
    {
        using var factory = ToolsFixture.Factory(); Assert.Throws<ArgumentOutOfRangeException>(() => new DeepSeekToolsClient(factory, ToolsFixture.Options(request: kind == "request" ? value : 1024, response: kind == "response" ? value : 1024))); Assert.Equal(0, factory.Creates);
    }
    /// <summary>默认地址属于 tools 原生服务，凭据不进入默认序列化或显示。</summary>
    [Fact]
    public void ConfigurationDoesNotSerializeOrDisplayKey()
    {
        var options = new DeepSeekToolsOptions { ApiKey = ToolsFixture.Key }; Assert.Equal("https://api.deepseek.com/v1/tools/", options.BaseAddress.AbsoluteUri); Assert.DoesNotContain(ToolsFixture.Key, JsonSerializer.Serialize(options)); Assert.DoesNotContain(ToolsFixture.Key, options.ToString()!);
        using var factory = ToolsFixture.Factory(); var client = new DeepSeekToolsClient(factory, options); Assert.DoesNotContain(ToolsFixture.Key, client.ToString()!); Assert.Equal(0, factory.Creates);
    }
    /// <summary>同类默认认证由明确 Key 覆盖；未选认证头不能混入请求，额外普通头保持。</summary>
    [Theory] [InlineData("same")] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")]
    public async Task FactoryDefaultsCannotSelectAnotherCredential(string kind)
    {
        using var factory = ToolsFixture.Factory(); factory.ConfigureClient = client => { if (kind == "same") client.DefaultRequestHeaders.Authorization = new("Basic", "other-key"); else client.DefaultRequestHeaders.Add(kind, "other-key"); client.DefaultRequestHeaders.Add("x-caller", "retained"); };
        factory.OnSend = (request, _) => { Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(ToolsFixture.Key, request.Headers.Authorization.Parameter); Assert.Equal("retained", Assert.Single(request.Headers.GetValues("x-caller"))); return Task.FromResult(ToolsFixture.Json(ToolsFixture.Response())); };
        var client = new DeepSeekToolsClient(factory, ToolsFixture.Options()); if (kind == "same") await client.SearchAsync(ToolsFixture.Request(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(ToolsFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(kind == "same" ? 1 : 0, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>HTTP(S)、显式端口和代理前缀均保留，只有固定 search/fetch 相对路径变化。</summary>
    [Theory] [InlineData(false, "http", false)] [InlineData(false, "https", true)] [InlineData(true, "http", true)] [InlineData(true, "https", false)]
    public async Task ExplicitPrefixAndPortRemainFixed(bool fetch, string scheme, bool slash)
    {
        var root = scheme + "://tools.example.invalid:8123/custom/v1/tools" + (slash ? "/" : ""); using var factory = ToolsFixture.Factory(fetch); factory.OnSend = (request, _) => { Assert.Equal(scheme + "://tools.example.invalid:8123/custom/v1/tools/" + (fetch ? "fetch" : "search"), request.RequestUri!.AbsoluteUri); return Task.FromResult(ToolsFixture.Json(ToolsFixture.Response(fetch))); };
        var result = await ToolsFixture.Invoke(new(factory, ToolsFixture.Options(root: root)), fetch, TestContext.Current.CancellationToken); Assert.Equal(scheme, result.Endpoint.Scheme); Assert.Equal(8123, result.Endpoint.Port); Assert.Equal(1, factory.Calls);
    }
    /// <summary>工厂完成前后取消都不能发起请求；已创建的客户端仍释放。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreSendCancellationDoesNotCreateAnAttempt(bool inFactory)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var factory = ToolsFixture.Factory(); if (inFactory) factory.OnCreate = cancellation.Cancel; else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeepSeekToolsClient(factory, ToolsFixture.Options()).SearchAsync(ToolsFixture.Request(), cancellation.Token)); Assert.Equal(0, factory.Calls); Assert.Equal(inFactory ? 1 : 0, factory.Creates); Assert.Equal(factory.Creates, factory.Disposals);
    }
    /// <summary>处理器原地改写请求方法或地址也不能绕过固定目标检查，读取正文之前明确失败。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task InPlaceRequestDriftIsDetected(bool fetch, bool method)
    {
        using var body = new ToolsReadStream(Encoding.UTF8.GetBytes(ToolsFixture.Response(fetch).GetRawText())); using var factory = ToolsFixture.Factory(fetch); factory.OnSend = (request, _) =>
        { if (method) request.Method = HttpMethod.Get; else request.RequestUri = new("https://different.invalid/tools/search"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body), RequestMessage = request }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), fetch, TestContext.Current.CancellationToken)); Assert.Equal(0, body.Reads); Assert.True(body.Disposed); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>无效声明长度不能按未知长度忽略，完整正文不提前读取。</summary>
    [Theory] [InlineData("bad")] [InlineData("-1")] [InlineData("1, 2")]
    public async Task InvalidDeclaredLengthCannotBeIgnored(string length)
    {
        using var body = new ToolsReadStream(Encoding.UTF8.GetBytes(ToolsFixture.Response().GetRawText())); using var factory = ToolsFixture.Factory(); factory.OnSend = (_, _) => { var content = new StreamContent(body); Assert.True(content.Headers.TryAddWithoutValidation("Content-Length", length)); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); };
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolsFixture.Invoke(new(factory, ToolsFixture.Options()), false, TestContext.Current.CancellationToken)); Assert.Equal(0, body.Reads); Assert.True(body.Disposed); Assert.Equal(1, factory.Disposals);
    }
}
