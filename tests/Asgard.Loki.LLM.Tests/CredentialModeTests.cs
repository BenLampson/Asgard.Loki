using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>显式无认证不放宽旧 API Key 契约；完整 Session 绑定仍跨客户端、并发和模式变化受校验。</summary>
public sealed class CredentialModeTests
{
    private readonly DistributedCacheLlmSessionStore _store = new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    /// <summary>API Key 的默认种类、序列化外壳和安全显示保持兼容。</summary>
    [Fact]
    public void ApiKey_KeepingLegacyConstructionAndSafeFormatting()
    {
        var value = new LlmCredential("identity", "fixture-secret"); Assert.Equal(LlmCredentialKind.ApiKey, value.Kind); Assert.Equal("fixture-secret", value.ApiKey);
        Assert.Equal("identity", value.ToString()); Assert.Equal("{\"Id\":\"identity\"}", JsonSerializer.Serialize(value));
    }
    /// <summary>无认证入口明确、不含密钥，旧供应商直接读取 Key 也不能得到空串后继续发请求。</summary>
    [Fact]
    public void Anonymous_HasExplicitIdentityAndNoReadableKey()
    {
        var value = LlmCredential.Anonymous("local-instance"); Assert.Equal(LlmCredentialKind.Anonymous, value.Kind); Assert.Equal("local-instance", value.ToString());
        Assert.Throws<InvalidOperationException>(() => value.ApiKey);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value)); Assert.False(json.RootElement.TryGetProperty("ApiKey", out _)); Assert.Equal(1, json.RootElement.GetProperty("Kind").GetInt32());
    }
    /// <summary>任何认证入口都要求明确稳定身份。</summary>
    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void InvalidIdentity_IsRejected(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => new LlmCredential(id!, "key")); Assert.ThrowsAny<ArgumentException>(() => LlmCredential.Anonymous(id!));
    }
    /// <summary>原构造器不以遗漏 Key 自动降级成无认证。</summary>
    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void MissingApiKey_IsNotAnonymous(string? key) => Assert.ThrowsAny<ArgumentException>(() => new LlmCredential("identity", key!));
    /// <summary>未实现新契约的供应商默认只支持 Key，第二个未选目标也必须预先校验。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void LegacyProvider_RejectsAnonymousEagerly(bool unselected)
    {
        var provider = new StubLlmProvider(); var target = Target() with { ProviderId = provider.Id };
        var targets = unselected ? new[] { target with { Id = "first", CredentialId = "key" }, target } : [target];
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(Configuration(provider, [LlmCredential.Anonymous("credential"), new("key", "fixture-key")], targets)));
        Assert.Empty(provider.Calls);
    }
    /// <summary>显式供应商声明也能拒绝 API Key，不能选择不兼容认证或隐藏备用目标。</summary>
    [Fact]
    public void DeclaredProvider_RejectsUnsupportedKeyKind()
    {
        var provider = new CredentialChatProvider(apiKey: false);
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(Configuration(provider, [new("credential", "fixture-key")], [Target()]))); Assert.Empty(provider.Calls);
    }
    /// <summary>四种完整/流式组合都跨客户端恢复相同服务器、模型、无认证身份和实现。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task AnonymousSession_RestoresCompleteTarget(bool firstStream, bool secondStream)
    {
        var provider = new CredentialChatProvider(); var targets = new[] { Target(), Target("second") };
        await using var first = Build(provider, LlmCredential.Anonymous("credential"), targets, 1); await Invoke(first, firstStream);
        await using var second = Build(provider, LlmCredential.Anonymous("credential"), targets, 0); await Invoke(second, secondStream);
        Assert.Equal(2, provider.Calls.Count);
        Assert.All(provider.Calls, call =>
        {
            Assert.Equal("second", call.Context.Target.Id); Assert.Equal("http://second.invalid/api/", call.Context.Target.BaseAddress.AbsoluteUri);
            Assert.Equal("native-second", call.Request["model"]!.GetValue<string>()); Assert.Equal("credential", call.Context.Credential.Id);
            Assert.Equal(LlmCredentialKind.Anonymous, call.Context.Credential.Kind); Assert.Throws<InvalidOperationException>(() => call.Context.Credential.ApiKey);
        });
    }
    /// <summary>同一个逻辑凭据 ID 更换认证种类时，旧 Session 不能沿用或重新选择其他服务器。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task KindChange_RejectsCachedSessionBeforeProvider(bool firstAnonymous)
    {
        var provider = new CredentialChatProvider();
        await using var first = Build(provider, Credential(firstAnonymous), [Target()]); await Invoke(first, false);
        await using var changed = Build(provider, Credential(!firstAnonymous), [Target(), Target("fallback")], 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed, false)); Assert.Single(provider.Calls);
    }
    /// <summary>无认证仍保持完整身份，修改服务器、模型、身份、实现或版本均使旧绑定失效。</summary>
    [Theory] [InlineData("server")] [InlineData("model")] [InlineData("identity")] [InlineData("implementation")] [InlineData("version")]
    public async Task AnonymousBinding_RejectsTargetChanges(string kind)
    {
        var provider = new CredentialChatProvider(); await using var first = Build(provider, Credential(true), [Target()]); await Invoke(first, false);
        var target = kind switch
        {
            "server" => Target() with { BaseAddress = new("http://changed.invalid/api/") }, "model" => Target() with { UpstreamModel = "changed" },
            "identity" => Target() with { CredentialId = "other" }, "version" => Target() with { Version = "2" }, _ => Target()
        };
        var nextProvider = kind == "implementation" ? new CredentialChatProvider(version: "2") : provider;
        var credential = LlmCredential.Anonymous(kind == "identity" ? "other" : "credential");
        await using var changed = Build(nextProvider, credential, [target]); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed, true));
        Assert.Single(provider.Calls); if (nextProvider != provider) Assert.Empty(nextProvider.Calls);
    }
    /// <summary>注册后的调用方集合变化不能把无认证目标静默换成 Key 认证。</summary>
    [Fact]
    public async Task CredentialCollection_IsSnapshotted()
    {
        var provider = new CredentialChatProvider(); LlmCredential[] credentials = [Credential(true)];
        await using var client = new LokiClientBuilder().AddLLM(Configuration(provider, credentials, [Target()])).Build();
        credentials[0] = new("credential", "replacement-key"); await Invoke(client, false);
        Assert.Equal(LlmCredentialKind.Anonymous, Assert.Single(provider.Calls).Context.Credential.Kind);
    }
    /// <summary>使用更新前的独立指纹算法建立真实缓存，默认 API Key 调用仍能恢复旧 Session。</summary>
    [Fact]
    public async Task LegacyApiKeyFingerprint_RemainsUsable()
    {
        var target = Target(); var provider = new CredentialChatProvider(); var credential = Credential(false);
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target) + "\nfixture-key\n" + provider.BindingIdentity)));
        await _store.GetOrCreateAsync(new("credential-session", "public", LlmProtocol.ChatCompletions, target.Id, legacy), TestContext.Current.CancellationToken);
        await using var client = Build(provider, credential, [target]); await Invoke(client, false);
        Assert.Equal(legacy, (await _store.GetAsync("credential-session", TestContext.Current.CancellationToken))!.Fingerprint); Assert.Single(provider.Calls);
    }
    /// <summary>不同首次选择通过显式协调收敛为一个完整无认证目标，不把普通 Get/Set 当原子操作。</summary>
    [Fact]
    public async Task ConcurrentAnonymousFirstBinding_RemainsOneTarget()
    {
        var provider = new CredentialChatProvider(); var targets = new[] { Target(), Target("second") };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrived = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == 20) ready.TrySetResult(); await ready.Task;
            await using var client = Build(provider, Credential(true), targets, index); await Invoke(client, index % 2 == 0);
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(20, provider.Calls.Count); Assert.Single(provider.Calls.Select(call => call.Context.Target.Id).Distinct());
        Assert.All(provider.Calls, call => Assert.Equal(LlmCredentialKind.Anonymous, call.Context.Credential.Kind));
    }
    /// <summary>每个测试客户端的配置独立，缓存和协调契约显式共享。</summary>
    private ILokiClient Build(ILlmProvider provider, LlmCredential credential, IReadOnlyList<LlmTarget> targets, int selected = 0)
        => new LokiClientBuilder().AddLLM(Configuration(provider, [credential], targets) with { TargetSelector = new TestTargetSelector(selected) }).Build();
    /// <summary>构造不依赖外部服务的完整 LLM 模块配置。</summary>
    private LlmOptions Configuration(ILlmProvider provider, IReadOnlyList<LlmCredential> credentials, IReadOnlyList<LlmTarget> targets)
        => new() { Providers = [provider], Credentials = credentials, SessionStore = _store, Routes = [new() { Model = "public", Targets = targets }] };
    /// <summary>显式选择 API Key 或无认证，不以空字符串作为开关。</summary>
    private static LlmCredential Credential(bool anonymous) => anonymous ? LlmCredential.Anonymous("credential") : new("credential", "fixture-key");
    /// <summary>两个候选服务器的实际模型与目标 ID 均不同。</summary>
    private static LlmTarget Target(string id = "first") => new()
    { Id = id, ProviderId = "credential-chat", BaseAddress = new("http://" + id + ".invalid/api/"), UpstreamModel = "native-" + id, CredentialId = "credential", Protocols = LlmProtocol.ChatCompletions };
    /// <summary>完整/流式入口均由公共客户端执行完整 Session 恢复。</summary>
    private static async Task Invoke(ILokiClient client, bool stream)
    {
        var request = new ChatCompletionRequest { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) };
        if (stream) { await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "credential-session" }, TestContext.Current.CancellationToken)) { } }
        else await client.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "credential-session" }, TestContext.Current.CancellationToken);
    }
}
