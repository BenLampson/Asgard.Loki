using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>独立黄金窗口和完整目标，只有内存缓存与替代 WebSocket。</summary>
internal static class SparkFixture
{
    internal static JsonElement Part(string name, string file = "spark.json")
    { using var stream = typeof(SparkFixture).Assembly.GetManifestResourceStream(typeof(SparkFixture).Namespace + ".Fixtures." + file)!; using var document = JsonDocument.Parse(stream); return document.RootElement.GetProperty(name).Clone(); }
    internal static JsonObject Body() => JsonNode.Parse(Part("core_wire").GetRawText())!.AsObject();
    internal static SparkRequest Request() => JsonSerializer.Deserialize<SparkRequest>(Part("caller"))!;
    internal static LlmTarget Target(string endpoint = "https://spark-api.xf-yun.com/v1.1/chat", string domain = "lite") => new()
    { Id = "native-target", ProviderId = "xunfei", BaseAddress = new(endpoint), UpstreamModel = domain, CredentialId = "spark-identity", Protocols = LlmProtocol.Spark };
    internal static LlmCredential Credential() => LlmCredential.External("spark-identity", "fixture-spark-principal");
    internal static LlmAdapterContext Context(string endpoint = "https://spark-api.xf-yun.com/v1.1/chat", string domain = "lite") => new(Target(endpoint, domain), Credential(), "public-spark", "native-session");
    internal static XunfeiSparkOptions Policy() => new() { AppId = "fixture-app-123" };
    internal static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    internal static ILokiClient Build(XunfeiSparkProvider provider, ILlmSessionStore? store = null) => new LokiClientBuilder().AddLLM(new()
    { Providers = [provider], Credentials = [Credential()], SessionStore = store ?? Store(), Routes = [new() { Model = "public-spark", Targets = [Target()] }] }).Build();
    internal static async Task<List<SparkEvent>> ReadAsync(XunfeiSparkProvider provider, CancellationToken token, JsonObject? body = null, LlmAdapterContext? context = null)
    { List<SparkEvent> events = []; await foreach (var item in provider.StreamAsync(body ?? Body(), context ?? Context(), token)) events.Add(item); return events; }
    internal static string[] Transcript()
    { using var stream = typeof(SparkFixture).Assembly.GetManifestResourceStream(typeof(SparkFixture).Namespace + ".Fixtures.spark.jsonl")!; using var reader = new StreamReader(stream); return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries); }
}
