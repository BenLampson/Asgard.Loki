using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>外部短期认证只能依附明确稳定主体；核心不持有令牌或放宽旧 Key/无认证契约。</summary>
public sealed class ExternalCredentialTests
{
    /// <summary>主体引用不会泄漏到 JSON/显示文本，也不能从旧 ApiKey getter 得到空 Bearer。</summary>
    [Fact]
    public void External_HasExplicitPrincipalAndNoKey()
    {
        var credential = LlmCredential.External("identity", "tenant/principal"); Assert.Equal(LlmCredentialKind.External, credential.Kind); Assert.Equal("tenant/principal", credential.PrincipalIdentity);
        Assert.Equal("identity", credential.ToString()); Assert.Equal("{\"Id\":\"identity\",\"Kind\":2}", JsonSerializer.Serialize(credential)); Assert.Throws<InvalidOperationException>(() => credential.ApiKey);
    }
    /// <summary>省略主体不能从其他认证种类借用身份。</summary>
    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void MissingPrincipal_IsRejected(string? principal) => Assert.ThrowsAny<ArgumentException>(() => LlmCredential.External("identity", principal!));
    /// <summary>外部身份仍要求稳定凭据 ID。</summary>
    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void MissingCredentialId_IsRejected(string? id) => Assert.ThrowsAny<ArgumentException>(() => LlmCredential.External(id!, "principal"));
    /// <summary>旧认证不能通过新 getter 假装声明了一个外部主体。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void OtherKinds_HaveNoExternalPrincipal(bool anonymous)
    {
        var credential = anonymous ? LlmCredential.Anonymous("identity") : new("identity", "fixture-key"); Assert.Throws<InvalidOperationException>(() => credential.PrincipalIdentity);
    }
    /// <summary>主体进入 JSON 指纹之前拒绝不可编码字符。</summary>
    [Fact]
    public void MalformedPrincipal_IsRejectedBeforeCache()
    {
        var provider = new CredentialChatProvider(external: true); var store = WebSocketFixture.Store();
        Assert.Throws<EncoderFallbackException>(() => Build(provider, LlmCredential.External("credential", "\ud800"), store)); Assert.Empty(provider.Calls); Assert.Equal(0, store.GetOrCreateCalls);
    }
    /// <summary>未声明或明确禁用外部认证的供应商在模块注册时失败，不等到实际取 Key。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnsupportedProvider_RejectsExternalEagerly(bool declared)
    {
        ILlmProvider provider = declared ? new CredentialChatProvider() : new StubLlmProvider(); var store = WebSocketFixture.Store();
        Assert.Throws<ArgumentException>(() => Build(provider, Credential(2), store)); Assert.Equal(0, store.GetOrCreateCalls);
    }
    /// <summary>完整/流式跨客户端恢复同一服务器、模型和主体，不因新选择器而调度其他目标。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Session_RestoresCompleteExternalTarget(bool firstStream, bool secondStream)
    {
        var provider = new CredentialChatProvider(external: true); var store = WebSocketFixture.Store(); var targets = new[] { Target(), Target("second") };
        await using (var first = Build(provider, Credential(2), store, targets, 1)) await Invoke(first, firstStream);
        await using (var second = Build(provider, Credential(2), store, targets)) await Invoke(second, secondStream);
        Assert.Equal(2, provider.Calls.Count); Assert.All(provider.Calls, call =>
        {
            Assert.Equal("second", call.Context.Target.Id); Assert.Equal("http://second.invalid/api/", call.Context.Target.BaseAddress.AbsoluteUri); Assert.Equal("native-second", call.Request["model"]!.GetValue<string>());
            Assert.Equal("tenant/principal", call.Context.Credential.PrincipalIdentity); Assert.Throws<InvalidOperationException>(() => call.Context.Credential.ApiKey);
            Assert.DoesNotContain("tenant/principal", call.Request.ToJsonString());
        });
    }
    /// <summary>同凭据 ID 改变主体也必须拒绝旧 Session，不能借换令牌名义换账户。</summary>
    [Fact]
    public async Task ChangedPrincipal_InvalidatesSession()
    {
        var provider = new CredentialChatProvider(external: true); var store = WebSocketFixture.Store();
        await using (var first = Build(provider, Credential(2), store)) await Invoke(first, false);
        await using var changed = Build(provider, LlmCredential.External("credential", "other/principal"), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed, true)); Assert.Single(provider.Calls);
    }
    /// <summary>三个认证种类使用不同指纹域，任意模式变化不能复用逻辑凭据 ID。</summary>
    [Theory] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(1, 0)] [InlineData(1, 2)] [InlineData(2, 0)] [InlineData(2, 1)]
    public async Task KindChange_DoesNotReuseExistingBinding(int firstKind, int nextKind)
    {
        var provider = new CredentialChatProvider(external: true); var store = WebSocketFixture.Store();
        await using (var first = Build(provider, Credential(firstKind), store)) await Invoke(first, false);
        await using var changed = Build(provider, Credential(nextKind), store); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(changed, false)); Assert.Single(provider.Calls);
    }
    /// <summary>主体/实现中的换行不能移动分隔符并制造相同身份指纹。</summary>
    [Fact]
    public async Task PrincipalAndProvider_UseUnambiguousFingerprintComponents()
    {
        var firstProvider = new CredentialChatProvider(external: true); var nextProvider = new CredentialChatProvider(version: "A\ncredential-chat-v1", external: true);
        const string firstPrincipal = "tenant\ncredential-chat-vA"; const string nextPrincipal = "tenant";
        Assert.Equal(firstPrincipal + "\n" + firstProvider.BindingIdentity, nextPrincipal + "\n" + nextProvider.BindingIdentity);
        var store = WebSocketFixture.Store(); await using (var first = Build(firstProvider, LlmCredential.External("credential", firstPrincipal), store)) await Invoke(first, false);
        await using var next = Build(nextProvider, LlmCredential.External("credential", nextPrincipal), store); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(next, false)); Assert.Empty(nextProvider.Calls);
    }
    /// <summary>用独立旧版字节算法创建实际缓存，新增认证种类不破坏既有 Key/匿名 Session。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LegacyKeyAndAnonymousFingerprint_RemainsValid(bool anonymous)
    {
        var provider = new CredentialChatProvider(external: true); var target = Target(); var store = WebSocketFixture.Store(); var serialized = JsonSerializer.Serialize(target);
        var source = anonymous ? "loki-llm:anonymous:v1\n" + serialized + "\n" + provider.BindingIdentity : serialized + "\nfixture-key\n" + provider.BindingIdentity;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        await store.GetOrCreateAsync(new("external-session", "public", LlmProtocol.ChatCompletions, target.Id, fingerprint), TestContext.Current.CancellationToken);
        await using var client = Build(provider, Credential(anonymous ? 1 : 0), store); await Invoke(client, false); Assert.Single(provider.Calls);
        Assert.Equal(fingerprint, (await store.GetAsync("external-session", TestContext.Current.CancellationToken))!.Fingerprint);
    }
    /// <summary>调用方更换凭据数组元素不能改变已经捕获的主体。</summary>
    [Fact]
    public async Task PrincipalCollection_IsSnapshotted()
    {
        var provider = new CredentialChatProvider(external: true); LlmCredential[] credentials = [Credential(2)];
        await using var client = new LokiClientBuilder().AddLLM(Options(provider, credentials, WebSocketFixture.Store(), [Target()], 0)).Build(); credentials[0] = LlmCredential.External("credential", "other");
        await Invoke(client, false); Assert.Equal("tenant/principal", Assert.Single(provider.Calls).Context.Credential.PrincipalIdentity);
    }
    /// <summary>并发首次绑定仍使用显式协调，只形成一个完整目标，而不是普通缓存 Get/Set 假原子操作。</summary>
    [Fact]
    public async Task ConcurrentFirstBinding_StillUsesOneCompleteTarget()
    {
        var provider = new CredentialChatProvider(external: true); var store = WebSocketFixture.Store(); var targets = new[] { Target(), Target("second") };
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async index => { await using var client = Build(provider, Credential(2), store, targets, index); await Invoke(client, index % 2 == 0); }));
        Assert.Equal(16, provider.Calls.Count); Assert.Single(provider.Calls.Select(call => call.Context.Target.Id).Distinct()); Assert.All(provider.Calls, call => Assert.Equal("tenant/principal", call.Context.Credential.PrincipalIdentity));
    }
    private static LlmCredential Credential(int kind) => kind switch { 0 => new("credential", "fixture-key"), 1 => LlmCredential.Anonymous("credential"), _ => LlmCredential.External("credential", "tenant/principal") };
    private static LlmTarget Target(string id = "first") => new() { Id = id, ProviderId = "credential-chat", BaseAddress = new("http://" + id + ".invalid/api/"), UpstreamModel = "native-" + id, CredentialId = "credential", Protocols = LlmProtocol.ChatCompletions };
    private static LlmOptions Options(ILlmProvider provider, IReadOnlyList<LlmCredential> credentials, ILlmSessionStore store, IReadOnlyList<LlmTarget> targets, int selected)
        => new() { Providers = [provider], Credentials = credentials, SessionStore = store, TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets }] };
    private static ILokiClient Build(ILlmProvider provider, LlmCredential credential, ILlmSessionStore store, IReadOnlyList<LlmTarget>? targets = null, int selected = 0)
        => new LokiClientBuilder().AddLLM(Options(provider, [credential], store, targets ?? [Target() with { ProviderId = provider.Id }], selected)).Build();
    private static async Task Invoke(ILokiClient client, bool stream)
    {
        var request = new ChatCompletionRequest { Model = "public", Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) };
        if (stream) { await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "external-session" }, TestContext.Current.CancellationToken)) { } }
        else await client.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "external-session" }, TestContext.Current.CancellationToken);
    }
}
