using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS 的项目/区域端点、实际模型、主体与适配配置构成完整 Session 绑定。</summary>
public sealed class MaaSBindingTests
{
    /// <summary>短期令牌正常轮换，跨客户端仍使用首次选中的完整目标。</summary>
    [Fact]
    public async Task RefreshedToken_PreservesSelectedTarget()
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); var second = MaaSFixture.Target("second") with { UpstreamModel = "meta/configured-other", BaseAddress = new("https://second.invalid/v1/projects/project-b/locations/global/endpoints/openapi") };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal(second.BaseAddress.AbsoluteUri + "/chat/completions", request.RequestUri!.AbsoluteUri); Assert.Equal("meta/configured-other", JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.GetProperty("model").GetString()); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString()); return MaaSFixture.Reply(); }); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock); var targets = new[] { MaaSFixture.Target(), second };
        await using (var client = MaaSFixture.Client(provider, store, targets, 1)) await MaaSFixture.Invoke(client); var before = await store.GetAsync("maas-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30);
        await using (var client = MaaSFixture.Client(provider, store, targets)) await MaaSFixture.Invoke(client); var after = await store.GetAsync("maas-session", TestContext.Current.CancellationToken); Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.DoesNotContain("fixture-token", JsonSerializer.Serialize(after)); Assert.DoesNotContain("principal-a", JsonSerializer.Serialize(after)); Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>任何完整目标或主体/配置变化都在再次取令牌前使旧绑定失败。</summary>
    [Theory] [InlineData("project")] [InlineData("location")] [InlineData("model")] [InlineData("server")] [InlineData("version")] [InlineData("principal")] [InlineData("source")] [InlineData("quota")] [InlineData("limit")] [InlineData("client")]
    public async Task ChangedIdentity_RejectsBoundSession(string change)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); var store = VertexFixture.Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); await using (var client = MaaSFixture.Client(new(factory, source, timeProvider: clock), store)) await MaaSFixture.Invoke(client);
        var target = MaaSFixture.Target(); var root = target.BaseAddress.AbsoluteUri; if (change == "project") root = root.Replace("project-a", "project-b"); if (change == "location") root = root.Replace("us-central1", "global"); if (change == "server") root = root.Replace("first.invalid", "second.invalid"); if (change == "version") root = root.Replace("/v1/", "/v1beta1/"); target = target with { BaseAddress = new(root), UpstreamModel = change == "model" ? "meta/configured-other" : MaaSFixture.Model }; if (change == "source") source.Version = "changed";
        var provider = new GoogleVertexMaaSLlmProvider(factory, source, new() { QuotaProjectId = change == "quota" ? "selected-quota" : null, MaximumBodyBytes = change == "limit" ? 1024 : 32 * 1024 * 1024 }, httpClientName: change == "client" ? "other" : "Asgard.Loki.LLM.GoogleVertex.MaaS", timeProvider: clock); await using var next = MaaSFixture.Client(provider, store, [target], credential: LlmCredential.External("identity", change == "principal" ? "principal-b" : "principal-a")); await Assert.ThrowsAsync<InvalidOperationException>(() => MaaSFixture.Invoke(next)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无效认证结果/配置漂移不发送、不重取、更不改用其他凭据。</summary>
    [Theory] [InlineData("principal")] [InlineData("expired")] [InlineData("null")] [InlineData("throw")] [InlineData("drift-before")] [InlineData("drift-during")]
    public async Task InvalidAuthentication_DoesNotRetry(string failure)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); source.Resolve = (principal, _) => { if (failure == "throw") throw new InvalidOperationException("source failure"); if (failure == "drift-during") source.Version = "changed"; return ValueTask.FromResult(failure == "null" ? null! : new GoogleVertexLlmAccessToken(failure == "principal" ? "other" : principal, "fixture-token", clock.Now.AddMinutes(failure == "expired" ? 0 : 60))); }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler); var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock); if (failure == "drift-before") source.Version = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(failure == "drift-before" ? 0 : 1, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>预取消/来源取消/建客户端取消与发送前到期都没有第二次认证或发送。</summary>
    [Theory] [InlineData("before")] [InlineData("source")] [InlineData("factory")] [InlineData("expiry")]
    public async Task CancellationAndExpiry_AcquireNoExtraResources(string phase)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new VertexClock(); var source = new VertexTokenSource(clock); if (phase == "before") cancel.Cancel(); if (phase == "source") source.Resolve = (principal, _) => { cancel.Cancel(); return ValueTask.FromResult(new GoogleVertexLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(MaaSFixture.Reply())); var factory = new VertexHttpFactory(handler) { OnCreate = () => { if (phase == "expiry") clock.Now = clock.Now.AddHours(2); else cancel.Cancel(); } }; var provider = new GoogleVertexMaaSLlmProvider(factory, source, timeProvider: clock);
        if (phase == "expiry") await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), cancel.Token)); else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), cancel.Token)); Assert.Equal(phase == "before" ? 0 : 1, source.Calls); Assert.Equal(0, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed));
    }
    /// <summary>只有已选 Bearer 与显式 quota 可以覆盖同类默认头，其他身份在发送前拒绝。</summary>
    [Theory] [InlineData("Authorization", false)] [InlineData("x-goog-api-key", false)] [InlineData("api-key", false)] [InlineData("x-api-key", false)] [InlineData("x-goog-user-project", false)] [InlineData("x-goog-user-project", true)]
    public async Task DefaultIdentity_IsChecked(string header, bool quota)
    {
        var clock = new VertexClock(); var source = new VertexTokenSource(clock); using var handler = new StubHttpHandler((request, _) => { Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); if (quota) Assert.Equal("selected-quota", Assert.Single(request.Headers.GetValues("x-goog-user-project"))); return Task.FromResult(MaaSFixture.Reply()); }); var factory = new VertexHttpFactory(handler) { DefaultHeader = header }; var provider = new GoogleVertexMaaSLlmProvider(factory, source, new() { QuotaProjectId = quota ? "selected-quota" : null }, timeProvider: clock); var success = quota || header == "Authorization";
        if (success) await provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(MaaSFixture.Request(), MaaSFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(success ? 1 : 0, handler.Calls); Assert.Equal(1, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
}
