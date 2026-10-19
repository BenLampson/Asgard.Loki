using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>独立原生金样与公共 SDK 装配；只使用内存缓存和替代 HTTP。</summary>
internal static class OllamaFixture
{
    internal const string Terminal = "{\"model\":\"native\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}";
    internal const string Partial = "{\"model\":\"native\",\"message\":{\"role\":\"assistant\",\"content\":\"你好🌏\"},\"done\":false}";
    /// <summary>独立资源不从生产映射器生成预期。</summary>
    internal static string Text(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Ollama.Tests.Fixtures." + name + ".json")!;
        using var reader = new StreamReader(source); return reader.ReadToEnd();
    }
    /// <summary>每次反序列化保持可变消息容器独立。</summary>
    internal static OllamaChatRequest Request() => JsonSerializer.Deserialize<OllamaChatRequest>(Text("request"))!;
    /// <summary>配置完整原生目标，根路径可包含显式反向代理前缀。</summary>
    internal static LlmTarget Target() => new() { Id = "native", ProviderId = "ollama", BaseAddress = new("http://native.invalid/prefix/api/"), UpstreamModel = "actual", CredentialId = "identity", Protocols = LlmProtocol.OllamaChat };
    /// <summary>直接适配器上下文与公共 SDK 路由使用相同原生契约。</summary>
    internal static LlmAdapterContext Context(bool anonymous = true) => new(Target(), Credential(anonymous), "public", "session");
    internal static LlmCredential Credential(bool anonymous) => anonymous ? LlmCredential.Anonymous("identity") : new("identity", "fixture-secret");
    internal static DistributedCacheLlmSessionStore Store() => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    /// <summary>调用者提供缓存以测试跨客户端绑定和调用快照。</summary>
    internal static ILokiClient Client(OllamaLlmProvider provider, bool anonymous = true, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [Credential(anonymous)], SessionStore = store ?? Store(),
            TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target()] }] }).Build();
    internal static JsonObject Payload() => JsonNode.Parse(Text("request"))!.AsObject();
    /// <summary>比较全部 JSON 字段，不只比较正文。</summary>
    internal static bool Same(string expected, JsonElement actual) => JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), actual);
}
