using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>签名来源/主体/到期在每个异步及交付边界保持固定，完全使用逻辑时钟。</summary>
public sealed class HunyuanIdentityTests
{
    /// <summary>来源配置漂移、错误主体和到期不能发送或继续使用原绑定。</summary>
    [Theory] [InlineData("source-before")] [InlineData("source-await")] [InlineData("principal")] [InlineData("expired")] [InlineData("factory-expired")] [InlineData("publish-expired")]
    public async Task IdentityAndExpiryAreCheckedAtEveryBoundary(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var clock = new HunyuanClock(); var expires = clock.Now.AddMinutes(1); source.Credentials = new(kind == "principal" ? "different-principal" : "fixture-tencent-principal", "AKIDFIXTURE", "fixture-secret", kind == "expired" ? clock.Now : expires); var provider = new TencentHunyuanProvider(factory, source, timeProvider: clock);
        if (kind == "source-before") source.BindingIdentity = "changed"; if (kind == "source-await") source.Acquire = _ => { source.BindingIdentity = "changed"; return ValueTask.FromResult(source.Credentials); }; if (kind == "factory-expired") factory.OnCreate = () => clock.Now = expires; if (kind == "publish-expired") factory.OnDisposeClient = () => clock.Now = expires;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(kind == "publish-expired" ? 1 : 0, factory.Calls); Assert.InRange(source.Calls, 0, 1);
    }
    /// <summary>原生请求预先取消时不获取凭据或创建 HTTP 客户端。</summary>
    [Fact]
    public async Task PreCancelledCallHasNoSideEffects()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel(); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TencentHunyuanProvider(factory, source).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), cancellation.Token)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>无效签名配置在构造阶段失败，不创建传输。</summary>
    [Theory] [InlineData("https://host")] [InlineData("host/path")] [InlineData("user@host")] [InlineData("host?query")] [InlineData("")] [InlineData("域名.example")]
    public void InvalidSigningHostCannotBeRegistered(string host)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); Assert.Throws<ArgumentException>(() => new TencentHunyuanProvider(factory, source, new() { SigningHost = host })); Assert.Equal(0, factory.Creates);
    }
    /// <summary>供应商配置变化改变完整绑定指纹，已有 Session 在认证前失败。</summary>
    [Theory] [InlineData("host")] [InlineData("region")] [InlineData("request-limit")] [InlineData("response-limit")] [InlineData("source")] [InlineData("client")]
    public async Task ProviderConfigurationDriftCannotReuseSession(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var store = HunyuanFixture.Store(); var clock = new HunyuanClock(); var original = new TencentHunyuanProvider(factory, source, timeProvider: clock);
        await using (var first = HunyuanFixture.Build(original, store)) await first.LLM.Hunyuan.CompleteAsync(HunyuanFixture.Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken);
        if (kind == "source") source.BindingIdentity = "changed-source";
        var options = kind switch { "host" => new TencentHunyuanOptions { SigningHost = "logical.example.invalid" }, "region" => new() { Region = "ap-guangzhou" }, "request-limit" => new() { MaximumRequestBytes = 2 * 1024 * 1024 }, "response-limit" => new() { MaximumResponseBytes = 16 * 1024 * 1024 }, _ => new() };
        var changed = new TencentHunyuanProvider(factory, source, options, httpClientName: kind == "client" ? "changed-client" : "Asgard.Loki.LLM.Tencent", timeProvider: clock); Assert.NotEqual(original.BindingIdentity, changed.BindingIdentity);
        await using var second = HunyuanFixture.Build(changed, store); await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Hunyuan.CompleteAsync(HunyuanFixture.Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
    /// <summary>来源错误或来源等待后的取消保持单次尝试，不创建 HTTP。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SourceFailureOrCancellationNeverRetries(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var failure = new IOException("fixture source failure");
        source.Acquire = _ => { if (cancel) { cancellation.Cancel(); return ValueTask.FromResult(source.Credentials); } return ValueTask.FromException<TencentHunyuanCredentials>(failure); };
        var error = await Record.ExceptionAsync(() => new TencentHunyuanProvider(factory, source).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), cancellation.Token)); if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.Same(failure, error); Assert.Equal(1, source.Calls); Assert.Equal(0, factory.Creates);
    }
    /// <summary>拼接式 API Key 不是 TC3 主体，不解析或借用网关约定。</summary>
    [Fact]
    public async Task PipeDelimitedKeyCannotSelectNativeAuthentication()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var context = new LlmAdapterContext(HunyuanFixture.Target(), new("external", "fake-app|fake-id|fake-secret"), "public", "invalid-auth");
        await Assert.ThrowsAsync<ArgumentException>(() => new TencentHunyuanProvider(factory, source).CompleteAsync(HunyuanFixture.Body(), context, TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, factory.Creates);
    }
}
