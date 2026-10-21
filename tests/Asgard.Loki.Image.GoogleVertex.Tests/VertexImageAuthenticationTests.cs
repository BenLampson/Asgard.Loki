using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>显式云认证来源、主体、有效期与借用所有权的离线边界。</summary>
public sealed class VertexImageAuthenticationTests
{
    /// <summary>认证主体、过期或来源配置错误均在第一次 HTTP 发送前失败。</summary>
    [Theory]
    [InlineData("principal")] [InlineData("expired")] [InlineData("source-before")]
    [InlineData("source-await")] [InlineData("source-factory")] [InlineData("expired-factory")]
    public async Task AuthenticationMismatchNeverSends(string fault)
    {
        using var source = new VertexTestTokenSource(); using var factory = new VertexTestFactory(); var clock = new VertexTestTime();
        var expiry = fault == "expired" ? clock.Now : clock.Now.AddMinutes(1);
        source.OnGet = _ => { if (fault == "source-await") source.BindingIdentity = "other"; return ValueTask.FromResult(new GoogleVertexImageAccessToken(fault == "principal" ? "other-principal" : "fixture-principal", "fixture-token-one", expiry)); };
        var adapter = new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(), clock);
        if (fault == "source-before") source.BindingIdentity = "other";
        if (fault == "source-factory") factory.OnCreate = () => source.BindingIdentity = "other";
        if (fault == "expired-factory") factory.OnCreate = () => clock.Now = clock.Now.AddMinutes(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Calls); Assert.Equal(fault.EndsWith("factory", StringComparison.Ordinal) ? 1 : 0, factory.Disposals);
        Assert.Equal(fault == "source-before" ? 0 : 1, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>SDK 不接管云登录、自动刷新或凭据重试。</summary>
    [Fact]
    public async Task AuthenticationFailureDoesNotRetryOrCreateClient()
    {
        using var source = new VertexTestTokenSource { OnGet = _ => throw new IOException("fixture token failure") }; using var factory = new VertexTestFactory();
        await Assert.ThrowsAsync<IOException>(() => new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(), new VertexTestTime()).GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>HTTP 已正确发送后令牌到期，不改变上游已完成的合法结果。</summary>
    [Fact]
    public async Task TokenExpiryAfterAuthorizedSendDoesNotInvalidateResult()
    {
        var clock = new VertexTestTime(); using var source = new VertexTestTokenSource { OnGet = _ => ValueTask.FromResult(new GoogleVertexImageAccessToken("fixture-principal", "fixture-token-one", clock.Now.AddSeconds(1))) };
        using var factory = new VertexTestFactory { OnSend = (_, _) => { clock.Now = clock.Now.AddMinutes(1); return Task.FromResult(VertexFixture.Json(VertexFixture.Part("response"))); } };
        Assert.Single((await new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(), clock).GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken)).Images);
        Assert.Equal(1, factory.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>不继承未配置的默认认证和项目身份。</summary>
    [Theory]
    [InlineData("Authorization")] [InlineData("x-goog-api-key")] [InlineData("x-goog-user-project")]
    public async Task UnboundDefaultIdentityFailsBeforeSend(string header)
    {
        using var factory = new VertexTestFactory { OnClient = client => client.DefaultRequestHeaders.TryAddWithoutValidation(header, header == "Authorization" ? "Bearer unrelated" : "unrelated") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleVertexImageAdapter(factory, VertexFixture.Options(key: true)).GenerateNativeAsync(VertexFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>主体、密钥与资源构造入口保持显式，不能自动降级认证或推断资源。</summary>
    [Theory]
    [InlineData("token-key")] [InlineData("key-principal")] [InlineData("token-express")]
    [InlineData("bare-model")] [InlineData("resource-traversal")]
    public void InvalidTargetFailsBeforeAuthentication(string fault)
    {
        using var source = new VertexTestTokenSource(); using var factory = new VertexTestFactory();
        var options = fault switch
        {
            "token-key" => VertexFixture.Options(key: true),
            "key-principal" => VertexFixture.Options(),
            "token-express" => VertexFixture.Options(express: true),
            _ => new GoogleVertexImageOptions { ModelResource = fault == "bare-model" ? "image-fixture" : "projects/p/locations/../publishers/google/models/m", BaseAddress = new("https://vertex.example.invalid/v1/"), ApiKey = "fixture" }
        };
        Assert.ThrowsAny<ArgumentException>(() => fault is "token-key" or "token-express" ? new GoogleVertexImageAdapter(factory, source, options) : new GoogleVertexImageAdapter(factory, options));
        Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>默认序列化和显示不包含短期令牌、主体断言或 API Key。</summary>
    [Fact]
    public void CredentialsAreAbsentFromDefaultDisplayAndJson()
    {
        var access = new GoogleVertexImageAccessToken("fixture-principal", "fixture-token-one", new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain("fixture-token-one", access.ToString()); Assert.DoesNotContain("fixture-token-one", JsonSerializer.Serialize(access)); Assert.DoesNotContain("fixture-principal", JsonSerializer.Serialize(access));
        Assert.DoesNotContain("fixture-image-key", JsonSerializer.Serialize(VertexFixture.Options(key: true)));
    }
    /// <summary>取消不启动认证/HTTP；工厂期间取消仍释放本次客户端。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationPreservesOwnership(bool inFactory)
    {
        using var canceled = new CancellationTokenSource(); using var source = new VertexTestTokenSource(); using var factory = new VertexTestFactory();
        if (inFactory) factory.OnCreate = canceled.Cancel; else canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GoogleVertexImageAdapter(factory, source, VertexFixture.Options(), new VertexTestTime()).GenerateNativeAsync(VertexFixture.Request(), canceled.Token));
        Assert.Equal(inFactory ? 1 : 0, source.Calls); Assert.Equal(inFactory ? 1 : 0, factory.Disposals); Assert.Equal(0, factory.Calls);
    }
}
