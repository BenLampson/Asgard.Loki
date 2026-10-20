using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>可选协议声明收窄物理接口能力，旧供应商行为和完整目标机制不变。</summary>
public sealed class ProtocolSupportTests
{
    /// <summary>不支持的单个/组合协议在注册阶段失败，不能等选中或发出请求后才发现。</summary>
    [Theory] [InlineData(LlmProtocol.None, LlmProtocol.ChatCompletions)]
    [InlineData(LlmProtocol.ChatCompletions, LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ChatCompletions, LlmProtocol.ChatCompletions | LlmProtocol.Responses)]
    public void RestrictedProtocol_IsRejectedBeforeInvocation(LlmProtocol allowed, LlmProtocol target)
    {
        var provider = new ProtocolRestrictedProvider(allowed);
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(Configuration(provider, [Target(provider.Id, target)])));
        Assert.Equal(0, provider.Calls); Assert.NotEmpty(provider.Checked);
    }

    /// <summary>备用目标也必须支持声明，不因当前选择其他服务器而跳过验证。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void AllConfiguredTargets_RequireDeclaredSupport(bool unsupportedFirst)
    {
        var provider = new ProtocolRestrictedProvider(LlmProtocol.ChatCompletions);
        var valid = Target(provider.Id, LlmProtocol.ChatCompletions); var invalid = Target(provider.Id, LlmProtocol.Responses) with { Id = "second" };
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(Configuration(provider, unsupportedFirst ? [invalid, valid] : [valid, invalid])));
        Assert.Equal(0, provider.Calls); Assert.Contains(LlmProtocol.Responses, provider.Checked);
    }

    /// <summary>肯定声明仍必须有实际适配接口，不能构造一个永远只能强制转换失败的目标。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Realtime)] [InlineData(LlmProtocol.Moderations)] [InlineData(LlmProtocol.TextCompletions)] [InlineData(LlmProtocol.Hunyuan)] [InlineData(LlmProtocol.Spark)]
    public void Declaration_CannotReplaceAdapterImplementation(LlmProtocol protocol)
    {
        var provider = new ProtocolClaimOnlyProvider();
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(Configuration(provider, [Target(provider.Id, protocol)])));
    }

    /// <summary>旧供应商完全不需要实现新契约，原有四种完整/流式调用继续执行。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task LegacyProvider_KeepsInterfaceBasedSupport(bool responses, bool stream)
    {
        var provider = new StubLlmProvider(); var protocol = responses ? LlmProtocol.Responses : LlmProtocol.ChatCompletions;
        await using var client = new LokiClientBuilder().AddLLM(Configuration(provider, [Target(provider.Id, protocol)])).Build();
        await Invoke(client, responses, stream); Assert.Single(provider.Calls);
    }

    /// <summary>声明接受的组合保留实际目标和既有调用形状，检查只接收单个已知协议值。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task DeclaredSupportedTargets_StillInvokeNativeAdapters(bool responses, bool stream)
    {
        var provider = new ProtocolRestrictedProvider(LlmProtocol.ChatCompletions | LlmProtocol.Responses);
        await using var client = new LokiClientBuilder().AddLLM(Configuration(provider, [Target(provider.Id, LlmProtocol.ChatCompletions | LlmProtocol.Responses)])).Build();
        await Invoke(client, responses, stream); Assert.Equal(1, provider.Calls);
        Assert.Equal(new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses }, provider.Checked);
    }

    /// <summary>非法协议位先按原规则拒绝，不让 None 或组合值进入声明回调。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(8192)]
    public void InvalidProtocolFlags_DoNotReachSupportCallback(int flags)
    {
        var provider = new ProtocolRestrictedProvider((LlmProtocol)(-1));
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(Configuration(provider, [Target(provider.Id, (LlmProtocol)flags)])));
        Assert.Empty(provider.Checked); Assert.Equal(0, provider.Calls);
    }

    /// <summary>测试只使用内存分布式缓存与明确测试协调契约。</summary>
    private static LlmOptions Configuration(ILlmProvider provider, IReadOnlyList<LlmTarget> targets) => new()
    {
        Providers = [provider], Credentials = [new("identity", "fixture-key")],
        SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
        Routes = [new() { Model = "public", Targets = targets }]
    };
    /// <summary>协议只是目标声明的一部分，服务器/模型/凭据和实现仍全部保留。</summary>
    private static LlmTarget Target(string id, LlmProtocol protocol) => new()
    { Id = "first", ProviderId = id, BaseAddress = new("https://stub.invalid/"), UpstreamModel = "model-a", CredentialId = "identity", Protocols = protocol };
    /// <summary>只通过公共 SDK 验证有效配置能实际调用，避免只测试反射接口存在。</summary>
    private static async Task Invoke(ILokiClient client, bool responses, bool stream)
    {
        var call = new LlmCallOptions { SessionId = "protocol-support" }; var token = TestContext.Current.CancellationToken;
        if (responses)
        {
            if (stream) await foreach (var _ in client.LLM.Responses.StreamAsync(new() { Model = "public", Input = "hi" }, call, token)) { }
            else await client.LLM.Responses.CreateAsync(new() { Model = "public", Input = "hi" }, call, token);
        }
        else
        {
            if (stream) await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(new() { Model = "public", Messages = [] }, call, token)) { }
            else await client.LLM.ChatCompletions.CompleteAsync(new() { Model = "public", Messages = [] }, call, token);
        }
    }
}
