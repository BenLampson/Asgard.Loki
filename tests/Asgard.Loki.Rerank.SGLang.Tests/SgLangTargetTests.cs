using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.SGLang.Tests;

/// <summary>完整目标、显式认证和取消边界，不启动或探测真实模型服务器。</summary>
public sealed class SgLangTargetTests
{
    /// <summary>基础地址、凭据、模式和字节策略在任何 HTTP 资源创建前校验。</summary>
    [Theory] [InlineData("query")] [InlineData("fragment")] [InlineData("credentials")] [InlineData("relative")] [InlineData("scheme")]
    [InlineData("key")] [InlineData("anonymous-key")] [InlineData("auth")] [InlineData("request-limit")] [InlineData("response-limit")]
    public void InvalidTargetsFailBeforeFactory(string kind)
    {
        var options = kind switch
        {
            "query" => SgLangFixture.Options(root: "https://target.example.invalid/v1/?q=1"),
            "fragment" => SgLangFixture.Options(root: "https://target.example.invalid/v1/#f"),
            "credentials" => SgLangFixture.Options(root: "https://user:pass@target.example.invalid/v1/"),
            "relative" => SgLangFixture.Options(root: "v1/"),
            "scheme" => SgLangFixture.Options(root: "ftp://target.example.invalid/v1/"),
            "key" => SgLangFixture.Options(key: "bad key"),
            "anonymous-key" => SgLangFixture.Options(SgLangRerankAuthentication.Anonymous, key: "unwanted-key"),
            "auth" => SgLangFixture.Options((SgLangRerankAuthentication)999),
            "request-limit" => SgLangFixture.Options(requestLimit: 0),
            _ => SgLangFixture.Options(responseLimit: 0)
        };
        using var factory = SgLangFixture.Factory(); Assert.ThrowsAny<ArgumentException>(() => new SgLangRerankAdapter(factory, options)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>两种显式模式都不会继承其他能力的认证、组织和项目头部。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuthenticationDoesNotInheritFactoryDefaults(bool bearer)
    {
        using var factory = SgLangFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated"); client.DefaultRequestHeaders.Add("api-key", "unrelated"); client.DefaultRequestHeaders.Add("x-api-key", "unrelated"); client.DefaultRequestHeaders.Add("OpenAI-Organization", "unrelated"); client.DefaultRequestHeaders.Add("OpenAI-Project", "unrelated"); };
        var options = SgLangFixture.Options(bearer ? SgLangRerankAuthentication.BearerToken : SgLangRerankAuthentication.Anonymous);
        factory.OnSend = (message, _) =>
        { if (bearer) Assert.Equal(options.ApiKey, message.Headers.Authorization!.Parameter); else Assert.Null(message.Headers.Authorization); foreach (var header in new[] { "api-key", "x-api-key", "OpenAI-Organization", "OpenAI-Project" }) Assert.False(message.Headers.Contains(header)); return Task.FromResult(SgLangFixture.Json(SgLangFixture.Part("responses")[0])); };
        await new SgLangRerankAdapter(factory, options).RerankAsync(SgLangFixture.Request(), TestContext.Current.CancellationToken);
    }
    /// <summary>配置不序列化密钥，借用的工厂保持调用方所有权。</summary>
    [Fact]
    public void OptionsExcludeSecretAndKeepExplicitBytePolicies()
    {
        var options = SgLangFixture.Options(); var json = JsonSerializer.Serialize(options); Assert.DoesNotContain(options.ApiKey, json); Assert.DoesNotContain("ApiKey", json);
        Assert.Equal(32 * 1024 * 1024, options.MaxRequestBytes); Assert.Equal(64 * 1024 * 1024, options.MaxResponseBytes);
    }
    /// <summary>预先取消不创建客户端，即使后续请求本身也无效。</summary>
    [Fact]
    public async Task PreCancelledCallCannotCreateFactoryClient()
    {
        using var factory = SgLangFixture.Factory(); using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(null!, source.Token)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>HTTP(S) 与有/无末尾斜杠均保留调用者原始服务前缀。</summary>
    [Theory] [InlineData("http", true)] [InlineData("http", false)] [InlineData("https", true)] [InlineData("https", false)]
    public async Task ExplicitServiceRootsPreservePrefix(string scheme, bool trailing)
    {
        using var factory = SgLangFixture.Factory(); var root = scheme + "://rerank.example.invalid/proxy/v1" + (trailing ? "/" : "");
        factory.OnSend = (message, _) => { Assert.Equal(scheme + "://rerank.example.invalid/proxy/v1/rerank", message.RequestUri!.AbsoluteUri); return Task.FromResult(SgLangFixture.Json(SgLangFixture.Part("responses")[0])); };
        await new SgLangRerankAdapter(factory, SgLangFixture.Options(root: root)).RerankAsync(SgLangFixture.Request(), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>工厂创建期间取消也不发送上游请求，已创建客户端正常释放。</summary>
    [Fact]
    public async Task FactoryCancellationStopsBeforeSend()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var factory = SgLangFixture.Factory(); factory.OnCreate = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SgLangRerankAdapter(factory, SgLangFixture.Options()).RerankAsync(SgLangFixture.Request(), cancellation.Token));
        Assert.Equal(1, factory.Creates); Assert.Equal(0, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }

}
