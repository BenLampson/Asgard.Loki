using System.Net;
using System.Text;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>以真实公共 SDK、MemoryDistributedCache 和本地替代 HTTP 构造压缩调用。</summary>
internal static class CompactionFixture
{
    /// <summary>包含保留项、工具关联、不透明压缩项和未来字段的完整上游窗口。</summary>
    internal const string Window = """
        {"id":"cmp_1","object":"response.compaction","created_at":1,"output":[
        {"id":"msg_1","type":"message","role":"user","content":[{"type":"input_text","text":"保留"}],"future":false},
        {"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"x\":0}"},
        {"type":"function_call_output","call_id":"call_1","output":"{\"ok\":false}"},
        {"id":"cp_1","type":"compaction","encrypted_content":"opaque-signed-state","future":{"value":null}}],
        "usage":{"input_tokens":15,"output_tokens":5,"total_tokens":20,"future_tokens":0},"future":{"accepted":true}}
        """;

    /// <summary>每个测试单独提供确定性的缓存和协调器。</summary>
    internal static DistributedCacheLlmSessionStore Store()
        => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());

    /// <summary>完整目标显式启用压缩，不隐式从 Responses 协议推断。</summary>
    internal static LlmTarget Target(string id = "a", LlmTargetFeatures features = LlmTargetFeatures.ResponsesCompaction) => new()
    {
        Id = id, ProviderId = "openai", BaseAddress = new($"https://{id}.invalid/custom/v1/"),
        UpstreamModel = "actual-" + id, CredentialId = "key-" + id, Protocols = LlmProtocol.Responses, Features = features
    };

    /// <summary>所有实例借用同一替代工厂，配置与 Session 生命周期由测试明确持有。</summary>
    internal static ILokiClient Build(StubHttpHandler handler, ILlmSessionStore? store = null,
        IReadOnlyList<LlmTarget>? targets = null, ILlmTargetSelector? selector = null, ILlmProvider? provider = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions
        {
            SessionStore = store ?? Store(), Providers = [provider ?? new OpenAiLlmProvider(new StubHttpClientFactory(handler))],
            Credentials = [new("key-a", "test-key-a"), new("key-b", "test-key-b")],
            Routes = [new LlmRoute { Model = "public", Targets = targets ?? [Target()] }],
            TargetSelector = selector ?? new RoundRobinLlmTargetSelector()
        }).Build();

    /// <summary>创建原生 JSON 响应。</summary>
    internal static HttpResponseMessage Json(string body = Window, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>明确的调用会话。</summary>
    internal static LlmCallOptions Call(string id = "session") => new() { SessionId = id };
}
