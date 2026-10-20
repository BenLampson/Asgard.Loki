using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>本地字节策略和既有完整绑定保持，不将分类 ID 当作 Responses 链或借用其他凭据。</summary>
public sealed class ModerationPolicyAndBindingTests
{
    /// <summary>新增可空策略省略时，原 HTTP/WS 绑定的默认序列化字节完全保持。</summary>
    [Fact]
    public void DefaultBindingIdentityKeepsPreviousBytes()
    {
        using var factory = ModerationFixture.Factory(); var provider = new OpenAiLlmProvider(factory);
        var previous = provider.GetType().AssemblyQualifiedName + "\nopenai-http-v2\n" + "{\"Options\":{\"MaximumBodyBytes\":33554432,\"MaximumBufferedTerminalEvents\":128},\"Client\":\"Asgard.Loki.LLM.OpenAI\"}";
        Assert.Equal(previous, provider.BindingIdentity); Assert.DoesNotContain("MaximumModerationRequestBytes", JsonSerializer.Serialize(new OpenAiHttpOptions()));
        var explicitPolicy = new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumModerationRequestBytes = 1024 });
        Assert.NotEqual(provider.BindingIdentity, explicitPolicy.BindingIdentity); Assert.Contains("\"MaximumModerationRequestBytes\":1024", explicitPolicy.BindingIdentity); Assert.Equal(0, factory.Creates);
    }
    /// <summary>非正请求策略在构造时失败，不等首次网络调用。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidRequestPolicyFailsAtConstruction(int maximum)
    {
        using var factory = ModerationFixture.Factory(); Assert.Throws<ArgumentOutOfRangeException>(() => ModerationFixture.Provider(factory, new() { MaximumModerationRequestBytes = maximum })); Assert.Equal(0, factory.Creates);
    }
    /// <summary>请求原始 UTF-8 字节恰到上限成功，少一字节在工厂前失败，不能按字符截断。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExactNativeRequestByteBoundary(bool below)
    {
        var expected = Encoding.UTF8.GetBytes(ModerationFixture.Part("requests")[3].GetRawText()); using var factory = ModerationFixture.Factory(3);
        factory.OnSend = async (message, token) => { Assert.Equal(expected, await message.Content!.ReadAsByteArrayAsync(token)); return ModerationFixture.Json(ModerationFixture.Part("responses")[3]); };
        var provider = ModerationFixture.Provider(factory, new() { MaximumModerationRequestBytes = expected.Length - (below ? 1 : 0) });
        if (below) { await Assert.ThrowsAsync<ArgumentException>(() => ModerationFixture.Invoke(provider, 3, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates); }
        else { await ModerationFixture.Invoke(provider, 3, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls); }
    }
    /// <summary>无效 CLR Unicode 不被序列化器替换；直接调用也在工厂前失败。</summary>
    [Fact]
    public async Task InvalidRequestUnicodeCannotReachFactory()
    {
        var request = ModerationFixture.Payload(); request["future"] = "\ud800"; using var factory = ModerationFixture.Factory(); var provider = ModerationFixture.Provider(factory);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => ((IModerationsAdapter)provider).CreateAsync(request, ModerationFixture.Context(provider), TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }
    /// <summary>固定目标认证不继承其他能力的密钥；调用者命名客户端的额外组织/项目头仍保留。</summary>
    [Fact]
    public async Task TargetAuthenticationDoesNotBorrowFactoryCredentials()
    {
        using var factory = ModerationFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); client.DefaultRequestHeaders.Add("api-key", "unrelated-key"); client.DefaultRequestHeaders.Add("x-api-key", "unrelated-key"); client.DefaultRequestHeaders.Add("OpenAI-Organization", "explicit-org"); client.DefaultRequestHeaders.Add("OpenAI-Project", "explicit-project"); };
        factory.OnSend = (message, _) =>
        {
            Assert.Equal("fixture-moderation-key-a", message.Headers.Authorization!.Parameter); Assert.False(message.Headers.Contains("api-key")); Assert.False(message.Headers.Contains("x-api-key"));
            Assert.Equal("explicit-org", Assert.Single(message.Headers.GetValues("OpenAI-Organization"))); Assert.Equal("explicit-project", Assert.Single(message.Headers.GetValues("OpenAI-Project")));
            return Task.FromResult(ModerationFixture.Json(ModerationFixture.Part("responses")[0]));
        };
        await ModerationFixture.Invoke(ModerationFixture.Provider(factory), 0, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>HTTP(S) 和可选末尾斜杠均保留显式前缀，不按模型名选择地址。</summary>
    [Theory] [InlineData("http", false)] [InlineData("http", true)] [InlineData("https", false)] [InlineData("https", true)]
    public async Task ExplicitApiRootsKeepFullPrefix(string scheme, bool slash)
    {
        using var factory = ModerationFixture.Factory(); var root = scheme + "://moderation.example.invalid/proxy/v1" + (slash ? "/" : "");
        factory.OnSend = (message, _) => { Assert.Equal(scheme + "://moderation.example.invalid/proxy/v1/moderations", message.RequestUri!.AbsoluteUri); return Task.FromResult(ModerationFixture.Json(ModerationFixture.Part("responses")[0])); };
        await using var client = ModerationFixture.Build(factory, targets: [ModerationFixture.Target(root: root)]);
        await client.LLM.Moderations.CreateAsync(ModerationFixture.Request(), new() { SessionId = "prefix" }, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>预先取消和工厂创建期间取消都不发送原生请求，已创建资源仍关闭。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeSendCannotPublish(bool factoryCancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var factory = ModerationFixture.Factory();
        if (factoryCancel) factory.OnCreate = cancellation.Cancel; else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModerationFixture.Invoke(ModerationFixture.Provider(factory), 0, cancellation.Token));
        Assert.Equal(0, factory.Calls); Assert.Equal(factoryCancel ? 1 : 0, factory.Creates); Assert.Equal(factoryCancel ? 1 : 0, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>实际 HTTP 请求继续绑定原服务器/凭据，只有新 Session 才选择第二个完整目标。</summary>
    [Fact]
    public async Task PublicHttpCallsRetainCompleteSessionTarget()
    {
        using var factory = ModerationFixture.Factory(); var seen = new List<(string Uri, string Key)>(); factory.OnSend = (message, _) =>
        { seen.Add((message.RequestUri!.AbsoluteUri, message.Headers.Authorization!.Parameter!)); return Task.FromResult(ModerationFixture.Json(ModerationFixture.Part("responses")[0])); };
        var a = ModerationFixture.Target("a", "https://a.example.invalid/v1/"); var b = ModerationFixture.Target("b", "https://b.example.invalid/v1/"); await using var client = ModerationFixture.Build(factory, targets: [a, b]);
        foreach (var session in new[] { "bound", "bound", "other" }) await client.LLM.Moderations.CreateAsync(ModerationFixture.Request(), new() { SessionId = session }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { ("https://a.example.invalid/v1/moderations", "fixture-moderation-key-a"), ("https://a.example.invalid/v1/moderations", "fixture-moderation-key-a"), ("https://b.example.invalid/v1/moderations", "fixture-moderation-key-b") }, seen);
    }
    /// <summary>显式改变策略影响完整适配身份，即使有效上限相同也不能沿用旧绑定。</summary>
    [Fact]
    public async Task ChangedExplicitPolicyRejectsExistingBinding()
    {
        using var factory = ModerationFixture.Factory(); var store = CompactionFixture.Store();
        await using (var first = ModerationFixture.Build(factory, store: store)) await first.LLM.Moderations.CreateAsync(ModerationFixture.Request(), new() { SessionId = "policy" }, TestContext.Current.CancellationToken);
        await using var second = ModerationFixture.Build(factory, new() { MaximumModerationRequestBytes = 32 * 1024 * 1024 }, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Moderations.CreateAsync(ModerationFixture.Request(), new() { SessionId = "policy" }, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>分类结果 ID 不进入 Responses 的 previous_response_id 归属索引。</summary>
    [Fact]
    public async Task ModerationIdsDoNotBecomeResponseChainOwnership()
    {
        using var factory = ModerationFixture.Factory(); var store = CompactionFixture.Store(); await using var client = ModerationFixture.Build(factory, store: store);
        var result = await client.LLM.Moderations.CreateAsync(ModerationFixture.Request(), new() { SessionId = "ownership" }, TestContext.Current.CancellationToken);
        var binding = await store.GetAsync("ownership", TestContext.Current.CancellationToken); Assert.NotNull(binding); Assert.Equal(LlmProtocol.Moderations, binding.Protocol);
        Assert.False(await store.OwnsResponseAsync(binding, result.Id!, TestContext.Current.CancellationToken));
    }
}
