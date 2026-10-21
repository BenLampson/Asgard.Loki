using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.MiniMax.Tests;

/// <summary>独立原生样本、完整目标与公共 SDK；缓存使用显式测试协调器。</summary>
internal static class MiniMaxFixture
{
    internal static JsonElement Golden
    {
        get { using var source = typeof(MiniMaxFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.MiniMax.Tests.Fixtures.native.json")!; using var document = JsonDocument.Parse(source); return document.RootElement.Clone(); }
    }
    internal static byte[] Bytes(string value) => new System.Text.UTF8Encoding(false, true).GetBytes(value);
    internal static string Frame(JsonElement value) => "data: "+JsonSerializer.Serialize(value)+"\n\n";
    internal static JsonObject Payload(bool stream = false) => JsonNode.Parse(Golden.GetProperty(stream ? "caller_stream" : "caller").GetRawText())!.AsObject();
    internal static LlmTarget Target(MiniMaxLlmProvider provider, int profile = 0) => new() { Id = "native", ProviderId = provider.Id, BaseAddress = new(Golden.GetProperty("profiles")[profile].GetProperty("root").GetString()!), UpstreamModel = Golden.GetProperty("model").GetString()!, CredentialId = "key", Protocols = LlmProtocol.ChatCompletions };
    internal static LlmAdapterContext Context(MiniMaxLlmProvider provider, int profile = 0, LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target(provider, profile), credential ?? new("key", Golden.GetProperty("profiles")[profile].GetProperty("key").GetString()!), "public", "session");
    internal static DistributedCacheLlmSessionStore Store() => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    internal static ILokiClient Build(MiniMaxLlmProvider provider, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null) => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], SessionStore = store ?? Store(), Credentials = [new("key", key ?? Golden.GetProperty("profiles")[profile].GetProperty("key").GetString()!)], Routes = [new() { Model = "public", Targets = [target ?? Target(provider, profile)] }] }).Build();
    internal static HttpResponseMessage Reply(Stream body, bool stream, long? length = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(body); content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); content.Headers.ContentLength = length; return new(status) { Content = content };
    }
    internal static async Task Drain(MiniMaxLlmProvider provider, CancellationToken token, List<ChatCompletionEvent> seen, JsonObject? payload = null, LlmAdapterContext? context = null)
    { await foreach (var item in provider.StreamAsync(payload ?? Payload(true), context ?? Context(provider), token)) seen.Add(item); }
    internal static bool Ending(ChatCompletionEvent item) => item.Data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null);
}
