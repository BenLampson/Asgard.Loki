using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Tests;

/// <summary>通过标准内存分布式缓存与显式协调器初始化公开 SDK。</summary>
internal static class WebSocketFixture
{
    /// <summary>创建无外部服务的共享存储。</summary>
    internal static ControlledSessionStore Store() => new(new DistributedCacheLlmSessionStore(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()));
    /// <summary>构造完整目标，不以单个模型的 Key 集合作为调度对象。</summary>
    internal static LlmTarget Target(string id = "a", LlmTargetFeatures features = LlmTargetFeatures.ResponsesWebSocket) => new()
    {
        Id = id, ProviderId = "ws", BaseAddress = new($"https://{id}.invalid/v1/"), UpstreamModel = "model-" + id,
        CredentialId = "key-" + id, Protocols = LlmProtocol.Responses, Features = features
    };
    /// <summary>公开模型可选择多个完整目标。</summary>
    internal static ILokiClient Build(StubResponsesWebSocketProvider provider, ILlmSessionStore? store = null,
        IReadOnlyList<LlmTarget>? targets = null, ILlmTargetSelector? selector = null) => new LokiClientBuilder().AddLLM(new()
    {
        SessionStore = store ?? Store(), Providers = [provider], Credentials = [new("key-a", "test-only-a"), new("key-b", "test-only-b")],
        TargetSelector = selector ?? new TestTargetSelector(0), Routes = [new LlmRoute { Model = "public", Targets = targets ?? [Target()] }]
    }).Build();
    /// <summary>明确传入调用者会话。</summary>
    internal static LlmCallOptions Call(string session = "session") => new() { SessionId = session };
}
