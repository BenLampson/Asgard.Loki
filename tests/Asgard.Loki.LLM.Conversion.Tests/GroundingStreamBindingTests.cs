using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Google;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>累计 grounding 校验的算法身份变化必须使旧 Session 失效，不暗中沿用旧语义。</summary>
public sealed class GroundingStreamBindingTests
{
    /// <summary>旧算法会话绑定与新算法身份不匹配时在第二次 HTTP 之前拒绝。</summary>
    [Theory] [InlineData("conversion-v22")] [InlineData("conversion-v24")]
    public async Task PreviousAlgorithmSession_IsRejectedBeforeHttp(string algorithm)
    {
        var store = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ConversionFixtures.Response(LlmProtocol.Gemini).ToJsonString(), Encoding.UTF8, "application/json") }));
        var provider = new ConvertingLlmProvider(new GoogleLlmProvider(new StubHttpClientFactory(handler)), LlmProtocol.Gemini, "conversion", GroundingStreamFixtures.Options);
        await using var old = Build(new LegacyGroundingBindingProvider(provider, algorithm), store); await using var current = Build(provider, store);
        var request = ConversionFixtures.Request(LlmProtocol.Gemini); request["model"] = "public";
        await ConvertingProviderTests.CompleteAsync(old, LlmProtocol.Gemini, request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertingProviderTests.CompleteAsync(current, LlmProtocol.Gemini, request));
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>除了受测算法身份之外，完整目标与借用缓存配置保持一致。</summary>
    private static ILokiClient Build(ILlmProvider provider, ILlmSessionStore store) => new LokiClientBuilder().AddLLM(new()
    {
        Providers = [provider], Credentials = [new("key", "bound-key")], SessionStore = store,
        Routes = [new() { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"),
            UpstreamModel = "model-a", CredentialId = "key", Protocols = LlmProtocol.Gemini }] }]
    }).Build();
}
