using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Mantle 仅使用显式稳定主体和借用凭据，不发现、借用或降级认证。</summary>
public sealed class MantleAuthenticationTests
{
    /// <summary>未选择的认证、签名和原生默认头都在发送前拒绝。</summary>
    [Theory]
    [InlineData("x-api-key")] [InlineData("api-key")] [InlineData("x-goog-api-key")]
    [InlineData("x-amz-security-token")] [InlineData("x-amz-future-auth")]
    [InlineData("anthropic-beta")] [InlineData("anthropic-workspace-id")] [InlineData("x-amzn-bedrock-trace")]
    public async Task UnselectedDefaults_DoNotReachUpstream(string header)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AwsHttpFactory(handler) { DefaultHeader = header }; var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls); Assert.Equal(1, source.Calls); var client = Assert.Single(factory.Clients); Assert.True(client.Disposed); Assert.Equal("stale", Assert.Single(client.DefaultRequestHeaders.GetValues(header))); Assert.False(source.Disposed);
    }
    /// <summary>同类默认签名和 Host 不覆盖最终所选值，也不会修改借用默认配置。</summary>
    [Theory] [InlineData("Authorization")] [InlineData("Host")] [InlineData("x-amz-date")] [InlineData("x-amz-content-sha256")] [InlineData("anthropic-version")]
    public async Task SelectedDefaults_AreOverriddenPerRequest(string header)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock);
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.DoesNotContain("stale", string.Join(',', request.Headers.GetValues(header)));
            Assert.Contains("/bedrock-mantle/aws4_request", Assert.Single(request.Headers.GetValues("Authorization")));
            return Task.FromResult(Reply(new MemoryStream(Bytes("{\"id\":\"msg\"}")), false));
        });
        var factory = new AwsHttpFactory(handler) { DefaultHeader = header }; var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        await provider.CreateAsync(Request(), Context(), TestContext.Current.CancellationToken); var client = Assert.Single(factory.Clients); Assert.Contains("stale", Assert.Single(client.DefaultRequestHeaders.GetValues(header))); Assert.Equal(1, handler.Calls);
    }
    /// <summary>普通 Key 和匿名凭据不是 SigV4 主体，两个方法均不能触及来源或工厂。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NonExternalCredential_RejectsBeforeAcquisition(bool anonymous)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" });
        var context = Context(credential: anonymous ? LlmCredential.Anonymous("identity") : new("identity", "not-an-access-key-pair"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateAsync(Request(), context, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => { await foreach (var _ in provider.StreamAsync(Request(), context, TestContext.Current.CancellationToken)) { } });
        Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>来源错误、错主体、空结果、过期、漂移和取消均不能自动重试或转 HTTP。</summary>
    [Theory]
    [InlineData("failure")] [InlineData("principal")] [InlineData("null")] [InlineData("expired")] [InlineData("drift")] [InlineData("cancel")]
    public async Task InvalidAcquisition_FailsWithoutFactory(string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock);
        source.Resolve = (principal, _) =>
        {
            if (fault == "failure") return ValueTask.FromException<AwsLlmSigningCredentials>(new IOException("source"));
            if (fault == "null") return ValueTask.FromResult<AwsLlmSigningCredentials>(null!);
            if (fault == "drift") source.Version = "changed"; if (fault == "cancel") cancel.Cancel();
            return ValueTask.FromResult(new AwsLlmSigningCredentials(fault == "principal" ? "other" : principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, fault == "expired" ? clock.Now : clock.Now.AddHours(1)));
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        var error = await Record.ExceptionAsync(() => provider.CreateAsync(Request(), Context(), cancel.Token));
        if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error); else if (fault == "failure") Assert.IsType<IOException>(error); else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls); Assert.False(source.Disposed);
    }
    /// <summary>工厂回调后的到期/漂移或取消仍在签名发送前拒绝。</summary>
    [Theory] [InlineData("expired")] [InlineData("drift")] [InlineData("cancel")]
    public async Task FactoryTimeChanges_FailBeforeSend(string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AwsHttpFactory(handler) { OnCreate = () => { if (fault == "expired") clock.Now = clock.Now.AddHours(2); if (fault == "drift") source.Version = "other"; if (fault == "cancel") cancel.Cancel(); } };
        var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var error = await Record.ExceptionAsync(() => provider.CreateAsync(Request(), Context(), cancel.Token));
        if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(0, handler.Calls);
    }
    /// <summary>预先取消及构造后来源配置漂移均在获取身份之前失败。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreflightFailure_DoesNotAcquire(bool drift)
    {
        using var cancel = new CancellationTokenSource(); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1" });
        if (drift) source.Version = "other"; else cancel.Cancel(); var error = await Record.ExceptionAsync(() => provider.CreateAsync(Request(), Context(), cancel.Token));
        if (drift) Assert.IsType<InvalidOperationException>(error); else Assert.IsAssignableFrom<OperationCanceledException>(error); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
