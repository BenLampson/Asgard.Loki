using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>短期令牌与稳定主体/完整资源绑定分离，不借认证失败重调度。</summary>
public sealed class AuthenticationTests
{
    /// <summary>新令牌和新客户端不改变已经绑定的主机、项目、区域和实际模型。</summary>
    [Fact]
    public async Task TokenRotation_PreservesCompleteTargetAcrossClients()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); var secondResource = "projects/project-b/locations/global/publishers/google/models/model-b";
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.EndsWith(secondResource + ":generateContent", request.RequestUri.AbsolutePath); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString()); return Task.FromResult(VertexFixture.Reply());
        });
        var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); var targets = new[] { VertexFixture.Target(), VertexFixture.Target("second", secondResource) };
        await using (var first = VertexFixture.Client(provider, store, targets, 1)) await VertexFixture.Invoke(first);
        var before = await store.GetAsync("vertex-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30);
        await using (var next = VertexFixture.Client(provider, store, targets)) await VertexFixture.Invoke(next);
        var after = await store.GetAsync("vertex-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.DoesNotContain("fixture-token", JsonSerializer.Serialize(after)); Assert.DoesNotContain("principal-a", JsonSerializer.Serialize(after));
        Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>完整目标或主体/配置改变，必须在再次取令牌之前拒绝旧 Session。</summary>
    [Theory] [InlineData("project")] [InlineData("location")] [InlineData("model")] [InlineData("server")] [InlineData("api-version")] [InlineData("principal")] [InlineData("source")] [InlineData("quota")] [InlineData("limit")] [InlineData("client")]
    public async Task ChangedIdentityOrConfiguration_RejectsExistingSession(string change)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler);
        await using (var first = VertexFixture.Client(new(factory, source, timeProvider: clock), store)) await VertexFixture.Invoke(first);
        var resource = change switch { "project" => VertexFixture.Resource.Replace("project-a", "project-b"), "location" => VertexFixture.Resource.Replace("us-central1", "global"), "model" => VertexFixture.Resource.Replace("configured-model", "other-model"), _ => VertexFixture.Resource };
        var target = VertexFixture.Target(resource: resource); if (change is "server" or "api-version") target = target with { BaseAddress = new(change == "server" ? "https://other.invalid/proxy/v1/" : "https://first.invalid/proxy/v1beta1/") };
        if (change == "source") source.Version = "source-v2";
        var provider = new GoogleVertexLlmProvider(factory, source, new() { QuotaProjectId = change == "quota" ? "quota" : null, MaximumBodyBytes = change == "limit" ? 1024 : 32 * 1024 * 1024 }, httpClientName: change == "client" ? "other-client" : "Asgard.Loki.LLM.GoogleVertex", timeProvider: clock);
        await using var next = VertexFixture.Client(provider, store, [target], principal: change == "principal" ? "principal-b" : "principal-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => VertexFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>错误主体、过期、空返回和配置漂移均不自动重取或发送。</summary>
    [Theory] [InlineData("principal")] [InlineData("expired")] [InlineData("null")] [InlineData("throw")] [InlineData("drift-before")] [InlineData("drift-during")]
    public async Task InvalidAuthentication_DoesNotDispatchOrRetry(string failure)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); source.Resolve = (principal, _) =>
        {
            if (failure == "throw") throw new InvalidOperationException("source failure"); if (failure == "drift-during") source.Version = "changed";
            return ValueTask.FromResult(failure == "null" ? null! : new GoogleVertexLlmAccessToken(failure == "principal" ? "other" : principal, "fixture-token", clock.Now.AddMinutes(failure == "expired" ? 0 : 60)));
        };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock); if (failure == "drift-before") source.Version = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(failure == "drift-before" ? 0 : 1, source.Calls); Assert.Equal(0, handler.Calls); Assert.Empty(factory.Clients); Assert.False(source.Disposed);
    }
    /// <summary>预取消、取令牌时取消、建立客户端时取消和发送前到期都不重试。</summary>
    [Theory] [InlineData("before")] [InlineData("source")] [InlineData("factory")] [InlineData("expiry")]
    public async Task CancellationAndExpiryBeforeSend_AcquireNoExtraResources(string phase)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new VertexClock(); var source = new VertexTokenSource(clock);
        if (phase == "before") cancel.Cancel();
        if (phase == "source") source.Resolve = (principal, _) => { cancel.Cancel(); return ValueTask.FromResult(new GoogleVertexLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(VertexFixture.Reply())); var factory = new VertexHttpFactory(handler) { OnCreate = () => { if (phase == "expiry") clock.Now = clock.Now.AddHours(2); else cancel.Cancel(); } }; var provider = new GoogleVertexLlmProvider(factory, source, timeProvider: clock);
        if (phase == "expiry") await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), cancel.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), cancel.Token));
        Assert.Equal(phase == "before" ? 0 : 1, source.Calls); Assert.Equal(0, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>默认 Key 或未声明项目不能混入请求，选定的同类 Bearer/项目头覆盖默认值。</summary>
    [Theory] [InlineData("Authorization", false)] [InlineData("x-goog-api-key", false)] [InlineData("api-key", false)] [InlineData("x-goog-user-project", false)] [InlineData("x-goog-user-project", true)]
    public async Task DefaultHeaders_CannotChangeSelectedIdentity(string header, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); if (quota) Assert.Equal("selected-quota", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); return Task.FromResult(VertexFixture.Reply());
        });
        var factory = new VertexHttpFactory(handler) { DefaultHeader = header }; var provider = new GoogleVertexLlmProvider(factory, source, new() { QuotaProjectId = quota ? "selected-quota" : null }, timeProvider: clock); var success = quota || header == "Authorization";
        if (success) await provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateAsync(VertexFixture.Request(), VertexFixture.Context(), TestContext.Current.CancellationToken));
        Assert.Equal(success ? 1 : 0, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
}
