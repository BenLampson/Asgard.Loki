using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>固定主体、认证方式、到期和配置状态贯穿压缩调用，不借用身份或重新取令牌。</summary>
public sealed class AzureCompactionAuthenticationTests
{
    /// <summary>认证前后或资源关闭期间的身份变化和到期不能交付成功。</summary>
    [Theory] [InlineData("source-before")] [InlineData("source-acquire")] [InlineData("source-create")] [InlineData("source-eof")] [InlineData("source-dispose")]
    [InlineData("principal")] [InlineData("expired-acquire")] [InlineData("expired-create")] [InlineData("expired-eof")] [InlineData("expired-dispose")] [InlineData("null")] [InlineData("non-ascii")]
    public async Task AuthenticationMustStayValidUntilPublication(string kind)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory(); var initial = clock.Now;
        source.Resolve = (principal, _) =>
        {
            if (kind == "source-acquire") source.Version = "changed";
            return ValueTask.FromResult(kind == "null" ? null! : new AzureLlmAccessToken(kind == "principal" ? "other-principal" : principal, kind == "non-ascii" ? "凭据" : "fixture-token", kind == "expired-acquire" ? initial : initial.AddMinutes(1)));
        };
        void Change() { if (kind.StartsWith("source", StringComparison.Ordinal)) source.Version = "changed"; else clock.Now = initial.AddMinutes(1); }
        if (kind.EndsWith("create", StringComparison.Ordinal)) factory.OnCreate = Change;
        if (kind.EndsWith("dispose", StringComparison.Ordinal)) factory.OnDisposeClient = Change;
        using var stream = new AzureCompactionReadStream(System.Text.Encoding.UTF8.GetBytes(AzureCompactionFixture.Part("response").GetRawText())) { OnEof = kind.EndsWith("eof", StringComparison.Ordinal) ? Change : null };
        factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock); if (kind == "source-before") Change();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompactAsync(AzureCompactionFixture.Payload(), AzureCompactionFixture.Context(true), TestContext.Current.CancellationToken));
        var dispatched = kind.EndsWith("eof", StringComparison.Ordinal) || kind.EndsWith("dispose", StringComparison.Ordinal);
        Assert.Equal(dispatched ? 1 : 0, factory.Calls); Assert.Equal(kind == "source-before" ? 0 : 1, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
        if (dispatched) { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); }
    }
    /// <summary>未选认证方式的默认头失败，同类默认值由本次明确凭据覆盖。</summary>
    [Theory] [InlineData(false, "other")] [InlineData(true, "other")] [InlineData(false, "x-api-key")] [InlineData(true, "x-api-key")] [InlineData(false, "same")] [InlineData(true, "same")]
    public async Task DefaultAuthenticationCannotSelectAnotherIdentity(bool external, string kind)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory(); factory.ConfigureClient = client =>
        {
            if (kind == "x-api-key") client.DefaultRequestHeaders.Add("x-api-key", "other-key");
            else if ((kind == "same") == external) client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
            else client.DefaultRequestHeaders.Add("api-key", "other-key");
            client.DefaultRequestHeaders.Add("x-caller", "retained");
        };
        factory.OnSend = (request, _) =>
        {
            Assert.Equal("retained", Assert.Single(request.Headers.GetValues("x-caller")));
            if (external) { Assert.Equal("fixture-token-1", request.Headers.Authorization!.Parameter); Assert.False(request.Headers.Contains("api-key")); }
            else { Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("api-key"))); Assert.Null(request.Headers.Authorization); }
            return Task.FromResult(AzureCompactionFixture.Json(AzureCompactionFixture.Part("response")));
        };
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        if (kind == "same") await provider.CompactAsync(AzureCompactionFixture.Payload(), AzureCompactionFixture.Context(external), TestContext.Current.CancellationToken);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompactAsync(AzureCompactionFixture.Payload(), AzureCompactionFixture.Context(external), TestContext.Current.CancellationToken));
        Assert.Equal(external ? 1 : 0, source.Calls); Assert.Equal(kind == "same" ? 1 : 0, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(source.Disposed);
    }
    /// <summary>预先取消、取令牌期间取消和创建客户端时取消均不发送上游。</summary>
    [Theory] [InlineData("before")] [InlineData("source")] [InlineData("factory")]
    public async Task CancellationBeforeDispatchNeverSends(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory();
        source.Resolve = (principal, _) => { if (kind == "source") cancellation.Cancel(); return ValueTask.FromResult(new AzureLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); };
        if (kind == "before") cancellation.Cancel(); if (kind == "factory") factory.OnCreate = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock).CompactAsync(AzureCompactionFixture.Payload(), AzureCompactionFixture.Context(true), cancellation.Token));
        Assert.Equal(kind == "before" ? 0 : 1, source.Calls); Assert.Equal(kind == "factory" ? 1 : 0, factory.Creates); Assert.Equal(factory.Creates, factory.Disposals); Assert.Equal(0, factory.Calls);
    }
}
