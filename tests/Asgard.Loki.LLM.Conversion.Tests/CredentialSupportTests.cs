using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.OpenAI;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>转换只代理固定上游的原生认证声明，不因为公开协议不同或允许损耗而降级认证。</summary>
public sealed class CredentialSupportTests
{
    /// <summary>每种固定上游声明均精确代理；不支持的未知种类也不能默认放行。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void ExplicitUpstreamSupport_IsDelegated(bool anonymous, bool key)
    {
        var provider = new ConvertingLlmProvider(new CredentialChatProvider(anonymous, key), LlmProtocol.ChatCompletions, "convert");
        Assert.Equal(anonymous, provider.SupportsCredential(LlmCredentialKind.Anonymous)); Assert.Equal(key, provider.SupportsCredential(LlmCredentialKind.ApiKey));
        Assert.False(provider.SupportsCredential((LlmCredentialKind)99));
    }
    /// <summary>外部主体支持由固定上游明确代理，普通损耗许可不能添加新的认证方式。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExternalSupport_IsDelegated(bool external)
    {
        var provider = new ConvertingLlmProvider(new CredentialChatProvider(external: external), LlmProtocol.ChatCompletions, "convert", new() { AllowLoss = true });
        Assert.Equal(external, provider.SupportsCredential(LlmCredentialKind.External));
    }
    /// <summary>公开转换入口保留同一外部主体，正文和上游结果 JSON 不注入认证材料。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicConversion_UsesExternalPrincipal(bool stream)
    {
        var upstream = new CredentialChatProvider(external: true); await using var client = Build(upstream, LlmCredential.External("credential", "tenant/principal"), Store());
        var request = CustomToolFixtures.Parse("""{"model":"public","input":"hello"}""");
        if (stream) await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, request); else await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, request);
        var call = Assert.Single(upstream.Calls); Assert.Equal("tenant/principal", call.Context.Credential.PrincipalIdentity); Assert.DoesNotContain("tenant/principal", call.Request.ToJsonString()); Assert.Throws<InvalidOperationException>(() => call.Context.Credential.ApiKey);
    }
    /// <summary>公开 Responses 到固定无认证 Chat 的完整/流式调用仍使用该身份，结果保留上游元数据。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicConversion_UsesDeclaredAnonymousUpstream(bool stream)
    {
        var upstream = new CredentialChatProvider(); await using var client = Build(upstream, LlmCredential.Anonymous("credential"), Store());
        var request = CustomToolFixtures.Parse("""{"model":"public","input":"hello"}""");
        if (stream)
        {
            var result = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, request);
            Assert.Equal("response.completed", result[^1].Data.GetProperty("type").GetString()); Assert.All(result, item => Assert.Empty(item.Metadata.Conversion!.Diagnostics));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, request);
            Assert.Equal("done", result.Data.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
        }
        var call = Assert.Single(upstream.Calls); Assert.Equal(LlmCredentialKind.Anonymous, call.Context.Credential.Kind); Assert.Equal("credential", call.Context.Credential.Id);
        Assert.Throws<InvalidOperationException>(() => call.Context.Credential.ApiKey);
    }
    /// <summary>未声明认证扩展的原生供应商仍只允许 Key；装饰器和损耗许可均不放宽该要求。</summary>
    [Fact]
    public void LegacyProvider_DoesNotGainAnonymousThroughConversion()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var native = new OpenAiLlmProvider(new StubHttpClientFactory(handler));
        var provider = new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions, "convert", new() { AllowLoss = true });
        Assert.True(provider.SupportsCredential(LlmCredentialKind.ApiKey)); Assert.False(provider.SupportsCredential(LlmCredentialKind.Anonymous));
        Assert.Throws<ArgumentException>(() => Build(native, LlmCredential.Anonymous("credential"), Store())); Assert.Equal(0, handler.Calls);
    }
    /// <summary>装饰器绑定也区分两种认证方式，旧 Session 不会在切换后再次请求上游。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuthenticationChange_InvalidatesConvertedSession(bool firstAnonymous)
    {
        var upstream = new CredentialChatProvider(); var store = Store();
        await using var first = Build(upstream, Credential(firstAnonymous), store);
        await ConvertingProviderTests.CompleteAsync(first, LlmProtocol.Responses, CustomToolFixtures.Parse("""{"model":"public","input":"hello"}"""));
        await using var changed = Build(upstream, Credential(!firstAnonymous), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertingProviderTests.StreamAsync(changed, LlmProtocol.Responses, CustomToolFixtures.Parse("""{"model":"public","input":"hello"}""")));
        Assert.Single(upstream.Calls);
    }
    /// <summary>所有配置由测试显式提供，使用 MemoryDistributedCache 和显式协调契约。</summary>
    private static ILokiClient Build(ILlmProvider upstream, LlmCredential credential, ILlmSessionStore store)
    {
        var provider = new ConvertingLlmProvider(upstream, LlmProtocol.ChatCompletions, "convert");
        return new LokiClientBuilder().AddLLM(new()
        {
            Providers = [provider], Credentials = [credential], SessionStore = store,
            Routes = [new() { Model = "public", Targets = [new() { Id = "fixed", ProviderId = provider.Id, BaseAddress = new("http://upstream.invalid/api/"),
                UpstreamModel = "actual", CredentialId = credential.Id, Protocols = LlmProtocol.Responses }] }]
        }).Build();
    }
    /// <summary>测试两种完整凭据，不以空 Key 表示开关。</summary>
    private static LlmCredential Credential(bool anonymous) => anonymous ? LlmCredential.Anonymous("credential") : new("credential", "fixture-key");
    /// <summary>每个测试拥有独立内存状态，不依赖外部 Redis。</summary>
    private static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
}
