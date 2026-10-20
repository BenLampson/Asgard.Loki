using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>独立完整原生字面窗口和实际 SDK，只有 Session 用例使用内存分布式缓存及显式协调器。</summary>
internal static class ArkFixture
{
    internal const string Key = "fixture-ark-key";
    internal static string Text(string name)
    { using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Volcengine.Tests.Fixtures." + name)!; using var reader = new StreamReader(source); return reader.ReadToEnd(); }
    internal static JsonElement Part(string name) { using var doc = JsonDocument.Parse(Text("ark.json")); return doc.RootElement.GetProperty(name).Clone(); }
    internal static JsonObject Payload(bool responses) => JsonNode.Parse(Part(responses ? "responses_caller" : "chat_caller").GetRawText())!.AsObject();
    internal static LlmTarget Target() => new() { Id = "ark-native", ProviderId = "volcengine", BaseAddress = new(Part("root").GetString()!), UpstreamModel = "configured-model", CredentialId = "ark-key" };
    internal static LlmAdapterContext Context(VolcengineLlmProvider provider, LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target() with { ProviderId = provider.Id }, credential ?? new("ark-key", Key), "public", "ark-session");
    internal static byte[] Bytes(string text) => new UTF8Encoding(false, true).GetBytes(text);
    internal static HttpResponseMessage Reply(Stream body, bool stream, long? length = null, int status = 200)
    { var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) }; result.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); result.Content.Headers.ContentLength = length; return result; }
    internal static async Task<JsonElement> Complete(VolcengineLlmProvider provider, bool responses, CancellationToken token, JsonObject? payload = null, LlmAdapterContext? context = null)
        => responses ? (await provider.CreateAsync(payload ?? Payload(true), context ?? Context(provider), token)).Data : (await provider.CompleteAsync(payload ?? Payload(false), context ?? Context(provider), token)).Data;
    internal static async Task<List<LlmJsonEvent>> Stream(VolcengineLlmProvider provider, bool responses, CancellationToken token, Action<LlmJsonEvent>? observe = null)
    {
        List<LlmJsonEvent> result = [];
        if (responses) await foreach (var item in ((IResponsesAdapter)provider).StreamAsync(Payload(true), Context(provider), token)) { observe?.Invoke(item); result.Add(item); }
        else await foreach (var item in provider.StreamAsync(Payload(false), Context(provider), token)) { observe?.Invoke(item); result.Add(item); }
        return result;
    }
    internal static bool Ending(JsonElement data) => data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "response.completed" or "response.incomplete" or "response.failed" or "error"
        || data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
        || data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null);
    internal static List<string> Frames(bool responses) { using var doc = JsonDocument.Parse(Text(responses ? "responses.json" : "chat.json")); return doc.RootElement.EnumerateArray().Select(x => x.GetRawText()).ToList(); }
    internal static string Wire(IEnumerable<string> frames, bool done) => string.Concat(frames.Select(x => "data: " + x + Environment.NewLine + Environment.NewLine)) + (done ? "data: [DONE]" + Environment.NewLine + Environment.NewLine : "");
    internal static ILokiClient Client(VolcengineLlmProvider provider, ILlmSessionStore? store = null, LlmTarget? target = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [new("ark-key", Key)], SessionStore = store ?? Store(), Routes = [new() { Model = "public", Targets = [target ?? Target()] }] }).Build();
    internal static DistributedCacheLlmSessionStore Store() => new(new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())), new TestSessionLock());
}
