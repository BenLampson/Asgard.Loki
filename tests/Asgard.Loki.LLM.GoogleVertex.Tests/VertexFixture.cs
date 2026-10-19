using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>独立完整协议金样和公开 SDK 装配，缓存使用真实内存实现与显式协调。</summary>
internal static class VertexFixture
{
    internal const string Resource = "projects/project-a/locations/us-central1/publishers/google/models/configured-model";
    internal static string Text(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.GoogleVertex.Tests.Fixtures." + name + ".json")!; using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static JsonObject Request() => JsonNode.Parse(Text("request"))!.AsObject();
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent(Text("response"), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(string id = "first", string resource = Resource) => new() { Id = id, ProviderId = "google-vertex", BaseAddress = new("https://" + id + ".invalid/proxy/v1/"), UpstreamModel = resource, CredentialId = "identity", Protocols = LlmProtocol.Gemini };
    internal static LlmAdapterContext Context() => new(Target(), LlmCredential.External("identity", "principal-a"), "public", "vertex-session");
    internal static ControlledSessionStore Store() => new(new DistributedCacheLlmSessionStore(new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
        Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())), new TestSessionLock()));
    internal static ILokiClient Client(GoogleVertexLlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, string principal = "principal-a", LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [credential ?? LlmCredential.External("identity", principal)], SessionStore = store ?? Store(), TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target()] }] }).Build();
    internal static Task<GeminiContent> Invoke(ILokiClient client) => client.LLM.Gemini.GenerateAsync(Request().Deserialize<GeminiContentRequest>()!, new() { SessionId = "vertex-session" }, TestContext.Current.CancellationToken);
}
