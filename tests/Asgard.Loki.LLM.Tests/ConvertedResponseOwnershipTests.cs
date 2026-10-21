using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>协议转换产生的 Responses 标识只属于输出 JSON，不得登记为原生可续接响应。</summary>
public sealed class ConvertedResponseOwnershipTests
{
    /// <summary>所有当前转换来源和原生对照都覆盖完整/流式入口，原始可见 ID 完全相同。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var source in new[] { LlmProtocol.None, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages,
            LlmProtocol.Gemini, LlmProtocol.BedrockConverse, LlmProtocol.BedrockNova })
        foreach (var stream in new[] { false, true }) yield return [source, stream];
    }
    /// <summary>真实内存缓存验证归属和续接，不靠 Mock 判断某个实现函数是否被调用。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task OnlyNativeResponsesIds_AreRegisteredForContinuation(LlmProtocol source, bool stream)
    {
        var provider = new ResponseOwnershipProvider(source); var token = TestContext.Current.CancellationToken;
        var store = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
        await using var client = new LokiClientBuilder().AddLLM(new()
        {
            Providers = [provider], Credentials = [new("identity", "fixture-key")], SessionStore = store,
            Routes = [new() { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id,
                BaseAddress = new("https://fixture.invalid/"), UpstreamModel = "native", CredentialId = "identity", Protocols = LlmProtocol.Responses }] }]
        }).Build();
        const string id = "loki_same_visible_id"; var call = new LlmCallOptions { SessionId = "response-ownership" };
        if (stream)
        {
            var seen = new List<ResponseEvent>();
            await foreach (var item in client.LLM.Responses.StreamAsync(new() { Model = "public", Input = "hi" }, call, token)) seen.Add(item);
            Assert.Equal(2, seen.Count); Assert.All(seen, item => Assert.Equal(id, item.Data.GetProperty("response").GetProperty("id").GetString()));
        }
        else
        {
            var result = await client.LLM.Responses.CreateAsync(new() { Model = "public", Input = "hi" }, call, token);
            Assert.Equal(id, result.Id);
        }
        var binding = await store.GetAsync(call.SessionId, token); Assert.NotNull(binding);
        Assert.Equal(source == LlmProtocol.None, await store.OwnsResponseAsync(binding, id, token));
        var next = new ResponseRequest { Model = "public", Input = "next", PreviousResponseId = id };
        if (source == LlmProtocol.None)
        { await client.LLM.Responses.CreateAsync(next, call, token); Assert.Equal(2, provider.Calls); }
        else
        { await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CreateAsync(next, call, token)); Assert.Equal(1, provider.Calls); }
    }
}
