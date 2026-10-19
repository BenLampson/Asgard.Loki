using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Anthropic;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>错误格式、上限和算法身份属于完整 Session，实现变化不能静默沿用旧绑定。</summary>
public sealed class ToolErrorBindingTests
{
    /// <summary>无论改变显式格式还是错误内容上限，都必须先于第二次 HTTP 拒绝旧会话。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedErrorPolicy_InvalidatesExistingSession(bool limit)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ToolErrorFixtures.Reply(LlmProtocol.ClaudeMessages, false)));
        var store = Store(); var firstOptions = ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiResponseObject);
        await using (var first = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, store, firstOptions))
            await ConvertingProviderTests.CompleteAsync(first, LlmProtocol.Gemini, Request());
        var changed = limit ? firstOptions with { MaxToolErrorBytes = firstOptions.MaxToolErrorBytes - 1 } : firstOptions with { ToolErrorFormat = LlmToolErrorFormat.GeminiErrorText };
        await using var next = ConvertingProviderTests.Build(handler, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages, store, changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertingProviderTests.CompleteAsync(next, LlmProtocol.Gemini, Request())); Assert.Equal(1, handler.Calls);
    }
    /// <summary>按旧 v23 选项字段和算法独立构造旧绑定，当前实现不能把它视为兼容缓存。</summary>
    [Fact]
    public async Task ActualLegacyV23OptionsAndIdentity_AreRejectedBeforeHttp()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ToolErrorFixtures.Reply(LlmProtocol.ClaudeMessages, false)));
        var native = new AnthropicLlmProvider(new StubHttpClientFactory(handler)); var options = new LlmConversionOptions { DefaultMaxTokens = 128 };
        var oldOptions = JsonSerializer.SerializeToNode(options)!.AsObject(); oldOptions.Remove("ToolErrorFormat"); oldOptions.Remove("MaxToolErrorBytes");
        var oldIdentity = $"{typeof(ConvertingLlmProvider).AssemblyQualifiedName}:conversion-v23:{native.Id}:{native.BindingIdentity}:{LlmProtocol.ClaudeMessages}:{oldOptions.ToJsonString()}";
        var converting = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "explicit-conversion", options); var store = Store();
        var request = Request(); ToolErrorFixtures.Result(request, LlmProtocol.Gemini)["response"] = JsonNode.Parse("{\"output\":\"ordinary\"}");
        await using (var first = Build(new LegacyToolErrorProvider(converting, oldIdentity), store)) await ConvertingProviderTests.CompleteAsync(first, LlmProtocol.Gemini, request);
        await using var next = Build(converting, store); await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertingProviderTests.CompleteAsync(next, LlmProtocol.Gemini, request)); Assert.Equal(1, handler.Calls);
    }
    private static JsonObject Request() { var request = ToolErrorFixtures.Read(LlmProtocol.Gemini, LlmToolErrorFormat.GeminiResponseObject); request["model"] = "public"; return request; }
    private static DistributedCacheLlmSessionStore Store() => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    /// <summary>同一个原生目标、凭据和存储，仅替换转换器真实实现身份。</summary>
    private static ILokiClient Build(ILlmProvider provider, ILlmSessionStore store) => new LokiClientBuilder().AddLLM(new LlmOptions
    {
        Providers = [provider], Credentials = [new("key", "bound-key")], SessionStore = store,
        Routes = [new() { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"), UpstreamModel = "model-a", CredentialId = "key", Protocols = LlmProtocol.Gemini }] }]
    }).Build();
}
