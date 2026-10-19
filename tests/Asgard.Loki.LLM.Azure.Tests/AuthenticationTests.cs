using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>稳定主体绑定和短期令牌分离，所有认证失败均在真实发送前停止。</summary>
public sealed class AuthenticationTests
{
    /// <summary>刷新令牌不改变 Session 的完整目标，跨客户端恢复也不重新调度。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RefreshedToken_PreservesSessionAndContinuation(bool responses)
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock); var store = AzureFixture.WebSocketStore();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("second.invalid", request.RequestUri!.Host); Assert.Equal("Bearer fixture-token-" + source.Calls, request.Headers.Authorization!.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (responses && source.Calls == 2) Assert.Equal("resp-native", body["previous_response_id"]!.GetValue<string>());
            Assert.DoesNotContain("fixture-token", body.ToJsonString()); return AzureFixture.Reply(responses);
        });
        var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        var targets = new[] { AzureFixture.Target(responses, AzureLlmApiStyle.V1), AzureFixture.Target(responses, AzureLlmApiStyle.V1, "second") };
        await using (var client = AzureFixture.Client(provider, responses, external: true, store: store, targets: targets, selected: 1)) await AzureFixture.Invoke(client, responses);
        var before = await store.GetAsync("azure-session", TestContext.Current.CancellationToken); clock.Now = clock.Now.AddMinutes(30);
        await using (var client = AzureFixture.Client(provider, responses, external: true, store: store, targets: targets))
        {
            var request = AzureFixture.Request(responses); if (responses) request["previous_response_id"] = "resp-native";
            await AzureFixture.Invoke(client, responses, request);
        }
        var after = await store.GetAsync("azure-session", TestContext.Current.CancellationToken);
        Assert.Equal(before!.Fingerprint, after!.Fingerprint); Assert.DoesNotContain("fixture-token", JsonSerializer.Serialize(after));
        Assert.Equal(2, source.Calls); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>不同主体、过期、空返回和源异常都不能重取令牌或尝试其他认证。</summary>
    [Theory] [InlineData("principal")] [InlineData("expired")] [InlineData("past")] [InlineData("null")] [InlineData("throw")] [InlineData("drift")]
    public async Task InvalidSourceResult_DoesNotSendOrRetry(string failure)
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        source.Resolve = (principal, _) =>
        {
            if (failure == "throw") throw new InvalidOperationException("source failure");
            if (failure == "drift") source.Version = "changed";
            return ValueTask.FromResult(failure == "null" ? null! : new AzureLlmAccessToken(failure == "principal" ? "other" : principal, "fixture-token", clock.Now.AddMinutes(failure == "expired" ? 0 : failure == "past" ? -1 : 60)));
        };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Reply(false))); var factory = new AzureHttpFactory(handler);
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: true), TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls); Assert.Equal(0, handler.Calls); Assert.Empty(factory.Clients); Assert.False(source.Disposed);
    }
    /// <summary>源等待期间取消、工厂建立时取消和发送前到期均不得发送。</summary>
    [Theory] [InlineData("source")] [InlineData("factory")] [InlineData("expiry")]
    public async Task CancellationOrExpiryBeforeSend_DoesNotRetry(string phase)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        if (phase == "source") source.Resolve = (principal, _) => { cancel.Cancel(); return ValueTask.FromResult(new AzureLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Reply(false)));
        var factory = new AzureHttpFactory(handler) { OnCreate = () => { if (phase == "expiry") clock.Now = clock.Now.AddHours(2); else cancel.Cancel(); } };
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        if (phase == "expiry") await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: true), cancel.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: true), cancel.Token));
        Assert.Equal(1, source.Calls); Assert.Equal(0, handler.Calls); Assert.All(factory.Clients, client => Assert.True(client.Disposed));
    }
    /// <summary>认证配置改变必须注册新实现，而新的实现身份不能沿用旧 Session。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedSourceIdentity_IsRejected(bool replaceProvider)
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock); var store = AzureFixture.WebSocketStore();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Reply(false))); var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        await using (var client = AzureFixture.Client(provider, false, external: true, store: store)) await AzureFixture.Invoke(client, false);
        source.Version = "different-tenant-or-audience"; if (replaceProvider) provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        await using var next = AzureFixture.Client(provider, false, external: true, store: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AzureFixture.Invoke(next, false)); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }
    /// <summary>同凭据 ID 换主体不能通过取得另一枚令牌绕过既有绑定。</summary>
    [Fact]
    public async Task ChangedPrincipal_IsRejectedBeforeTokenSource()
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock); var store = AzureFixture.WebSocketStore();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Reply(false))); var provider = new AzureLlmProvider(new AzureHttpFactory(handler), tokenSource: source, timeProvider: clock);
        await using (var client = AzureFixture.Client(provider, false, external: true, store: store)) await AzureFixture.Invoke(client, false);
        await using var changed = AzureFixture.Client(provider, false, external: true, store: store, principal: "other/principal");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AzureFixture.Invoke(changed, false)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同类默认头被本次选择覆盖，异类默认认证头必须拒绝，不修改借用工厂。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task DefaultAuthenticationHeaders_AreIsolated(bool external, bool conflict)
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        using var handler = new StubHttpHandler((request, _) =>
        {
            if (external) { Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("api-key")); }
            else { Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("api-key"))); Assert.Null(request.Headers.Authorization); }
            return Task.FromResult(AzureFixture.Reply(false));
        });
        var factory = new AzureHttpFactory(handler) { DefaultBearer = external != conflict ? "stale" : null, DefaultApiKey = external == conflict ? "stale" : null };
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        if (conflict) await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: external), TestContext.Current.CancellationToken));
        else await provider.CompleteAsync(AzureFixture.Request(false), AzureFixture.Context(false, external: external), TestContext.Current.CancellationToken);
        Assert.Equal(conflict ? 0 : 1, handler.Calls); Assert.Equal(external ? 1 : 0, source.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>等待认证源前快照原生请求；调用方后续修改不改变发送报文。</summary>
    [Fact]
    public async Task DirectAdapter_SnapshotsBeforeAuthenticationAwait()
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, "fixture-token", clock.Now.AddHours(1)); };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AzureFixture.Text("chat-v1-wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return AzureFixture.Reply(false); });
        var provider = new AzureLlmProvider(new AzureHttpFactory(handler), tokenSource: source, timeProvider: clock); var input = AzureFixture.Request(false);
        var pending = provider.CompleteAsync(input, AzureFixture.Context(false, external: true), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); await pending; Assert.Equal(1, source.Calls);
    }
}
