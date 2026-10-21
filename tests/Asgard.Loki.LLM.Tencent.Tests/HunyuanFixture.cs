using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>独立原生信封/签名向量和内存缓存目标，所有 HTTP/认证仅为离线替身。</summary>
internal static class HunyuanFixture
{
    internal static JsonElement Part(string name, string file = "hunyuan.json")
    { using var stream = typeof(HunyuanFixture).Assembly.GetManifestResourceStream(typeof(HunyuanFixture).Namespace + ".Fixtures." + file)!; using var doc = JsonDocument.Parse(stream); return doc.RootElement.GetProperty(name).Clone(); }
    internal static JsonObject Body() => JsonNode.Parse(Part("wire_complete").GetRawText())!.AsObject();
    internal static HunyuanRequest Request() => JsonSerializer.Deserialize<HunyuanRequest>(Part("caller"))!;
    internal static LlmTarget Target(string endpoint = "https://hunyuan.tencentcloudapi.com/") => new()
    { Id = "native-target", ProviderId = "tencent", BaseAddress = new(endpoint), UpstreamModel = "native-fixed", CredentialId = "external", Protocols = LlmProtocol.Hunyuan };
    internal static LlmCredential Credential() => LlmCredential.External("external", "fixture-tencent-principal");
    internal static LlmAdapterContext Context(string endpoint = "https://hunyuan.tencentcloudapi.com/") => new(Target(endpoint), Credential(), "public", "native-session");
    internal static HttpResponseMessage Reply(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static ILokiClient Build(TencentHunyuanProvider provider, ILlmSessionStore? store = null, LlmTarget? target = null) => new LokiClientBuilder().AddLLM(new()
    { Providers = [provider], Credentials = [Credential()], SessionStore = store ?? Store(), Routes = [new() { Model = "public", Targets = [target ?? Target()] }] }).Build();
    internal static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    internal static JsonObject StreamBody() => JsonNode.Parse(Part("wire_stream").GetRawText())!.AsObject();
    internal static string NativeSse(string file = "hunyuan.sse")
    { using var stream = typeof(HunyuanFixture).Assembly.GetManifestResourceStream(typeof(HunyuanFixture).Namespace + ".Fixtures." + file)!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    internal static string Frames(IEnumerable<JsonElement> events) => string.Concat(events.Select(e => "data: " + e.GetRawText() + "\r\n\r\n"));
    internal static HttpResponseMessage Sse(string? text = null)
    { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text ?? NativeSse(), Encoding.UTF8, "text/event-stream") }; return response; }
    internal static async Task<List<HunyuanEvent>> ReadAsync(TencentHunyuanProvider provider, CancellationToken token, JsonObject? body = null, LlmAdapterContext? context = null)
    { List<HunyuanEvent> items = []; await foreach (var item in provider.StreamAsync(body ?? StreamBody(), context ?? Context(), token)) items.Add(item); return items; }
}
