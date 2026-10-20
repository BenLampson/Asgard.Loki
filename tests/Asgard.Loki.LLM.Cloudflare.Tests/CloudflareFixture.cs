using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Cloudflare.Tests;

/// <summary>独立原生字面样本、完整目标与内存缓存，不访问真实 HTTP 或账号。</summary>
internal static class CloudflareFixture
{
    internal const string NativeRecord = "{\"response\":\"你好🌏\",\"future\":null}";
    internal const string Complete = "{\"result\":{\"response\":\"\"},\"success\":true,\"errors\":[],\"messages\":[]}";
    internal const string Done = "data: [DONE]\n\n";
    internal static string Text(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Cloudflare.Tests.Fixtures." + name)!;
        using var reader = new StreamReader(source); return reader.ReadToEnd();
    }
    internal static JsonElement Part(string name)
    { using var document = JsonDocument.Parse(Text("native.json")); return document.RootElement.GetProperty(name).Clone(); }
    internal static WorkersAiTextRequest Request() => JsonSerializer.Deserialize<WorkersAiTextRequest>(Part("caller"))!;
    internal static JsonObject Payload() => JsonNode.Parse(Part("wire_complete").GetRawText())!.AsObject();
    internal static LlmTarget Target() => new()
    { Id = "workers-account", ProviderId = "cloudflare-text", BaseAddress = new(Part("root").GetString()!), UpstreamModel = Part("model").GetString()!, CredentialId = "workers-key", Protocols = LlmProtocol.WorkersAiText };
    internal static LlmAdapterContext Context(LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target(), credential ?? new("workers-key", "fixture-selected-token"), "public-workers-text", "native");
    internal static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    internal static ILokiClient Client(CloudflareTextProvider provider, ILlmSessionStore? store = null) => new LokiClientBuilder().AddLLM(new()
    { Providers = [provider], Credentials = [new("workers-key", "fixture-selected-token")], SessionStore = store ?? Store(), Routes = [new() { Model = "public-workers-text", Targets = [Target()] }] }).Build();
    internal static StubHttpHandler Reply(Stream source, bool stream = false, int status = 200) => new((_, _) =>
    { var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(source) }; response.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); return Task.FromResult(response); });
    internal static string Frame(string data) => "data: " + data + "\n\n";
    internal static async Task Invoke(CloudflareTextProvider provider, bool stream, CancellationToken? token = null)
    {
        if (stream) await foreach (var _ in provider.StreamAsync(Payload(), Context(), token ?? Xunit.TestContext.Current.CancellationToken)) { }
        else await provider.CompleteAsync(Payload(), Context(), token ?? Xunit.TestContext.Current.CancellationToken);
    }
}
