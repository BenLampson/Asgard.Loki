using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>独立原生样本和完整目标装配，测试不访问真实 HTTP 或 Redis。</summary>
internal static class CohereFixture
{
    internal const string Start = "{\"event_type\":\"stream-start\",\"generation_id\":\"g\"}";
    internal const string Partial = "{\"event_type\":\"text-generation\",\"text\":\"你好🌏\"}";
    internal const string End = "{\"event_type\":\"stream-end\",\"finish_reason\":\"COMPLETE\",\"response\":{\"generation_id\":\"g\",\"response_id\":\"different\",\"text\":\"你好🌏\"}}";
    internal static string Text(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Cohere.Tests.Fixtures." + name)!;
        using var reader = new StreamReader(source); return reader.ReadToEnd();
    }
    internal static CohereChatRequest Request() => JsonSerializer.Deserialize<CohereChatRequest>(Text("request.json"))!;
    internal static JsonObject Payload() => JsonNode.Parse(Text("request.json"))!.AsObject();
    internal static LlmTarget Target() => new()
    { Id = "target", ProviderId = "cohere", CredentialId = "key", BaseAddress = new("https://native.invalid/proxy/v1/"), UpstreamModel = "command-configured-by-target", Protocols = LlmProtocol.CohereChat };
    internal static LlmAdapterContext Context(LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target(), credential ?? new("key", "fixture-selected-key"), "public-cohere", "native");
    internal static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    internal static ILokiClient Client(CohereLlmProvider provider, ILlmSessionStore? store = null, LlmTarget? target = null) => new LokiClientBuilder().AddLLM(new LlmOptions
    { Providers = [provider], Credentials = [new("key", "fixture-selected-key")], SessionStore = store ?? Store(), Routes = [new() { Model = "public-cohere", Targets = [target ?? Target()] }] }).Build();
    internal static bool Same(string expected, JsonElement actual) => JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), actual);
}
