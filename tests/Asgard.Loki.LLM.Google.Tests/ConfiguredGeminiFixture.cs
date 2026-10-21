using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>原生 Gemini 上游独立完整样本及公共 SDK 的完整固定目标。</summary>
internal static class ConfiguredGeminiFixture
{
    internal static JsonElement Window { get { using var stream = typeof(ConfiguredGeminiFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.Google.Tests.configured-http.json")!; using var json = JsonDocument.Parse(stream); return json.RootElement.Clone(); } }
    internal static JsonElement Profile(int profile = 0) => Window.GetProperty("profiles")[profile];
    internal static DistributedCacheLlmSessionStore Store() => new(new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    internal static LlmTarget Target(int profile = 0) => new() { Id = "configured-gemini-target", ProviderId = "configured-gemini-upstream", BaseAddress = new(Window.GetProperty("root").GetString()!), UpstreamModel = Window.GetProperty("model").GetString()!, CredentialId = "native-key", Protocols = LlmProtocol.Gemini, Version = "configured-v1" };
    internal static GoogleHttpOptions Options(int profile = 0) => new()
    {
        Authentication = Enum.Parse<GoogleHttpAuthentication>(Profile(profile).GetProperty("authentication").GetString()!),
        Transport = new() { GenerateContentPath = "gemini/{model}:generate?existing=1&reference=https://example.invalid/a&tag=1&tag=2", StreamGenerateContentPath = "gemini/{model}:stream?alt=sse&existing=1&tag=1&tag=2", CredentialName = Profile(profile).GetProperty("name").GetString(), CredentialPrefix = Profile(profile).GetProperty("prefix").GetString()!, CredentialSuffix = Profile(profile).GetProperty("suffix").GetString()! }
    };
    internal static GoogleLlmProvider Provider(GoogleHttpFactory factory, int profile = 0, GoogleHttpOptions? options = null, string client = "Explicit.Gemini") => new(factory, options ?? Options(profile), "configured-gemini-upstream", client);
    internal static ILokiClient Build(GoogleLlmProvider provider, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], SessionStore = store ?? Store(), Credentials = [credential ?? (profile == 2 ? LlmCredential.Anonymous("native-key") : new("native-key", Profile(profile).GetProperty("key").GetString()!))], Routes = [new() { Model = "public", Targets = [target ?? Target(profile)] }] }).Build();
    internal static GeminiContentRequest Request() => Window.GetProperty("caller").Deserialize<GeminiContentRequest>()!;
    internal static byte[] Bytes(string value) => new UTF8Encoding(false, true).GetBytes(value);
    internal static string Frame(JsonElement value) => "data: "+JsonSerializer.Serialize(value)+"\n\n";
    internal static string CleanSse => string.Concat(Window.GetProperty("events").EnumerateArray().Where(e => !e.TryGetProperty("type", out _)).Select(Frame));
    internal static HttpResponseMessage Reply(Stream body, bool stream = false, HttpStatusCode status = HttpStatusCode.OK, long? length = null)
    { var content = new StreamContent(body); content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); content.Headers.ContentLength = length; return new(status) { Content = content }; }
    internal static bool Ending(GeminiContentEvent item) => !item.Data.TryGetProperty("type", out _) && (item.Data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null || item.Data.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _) || item.Data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any(c => c.TryGetProperty("finishReason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is { Length: > 0 } reason && reason != "FINISH_REASON_UNSPECIFIED"));
    /// <summary>成功和失败都检查实际调用对象未被 SDK 改写。</summary>
    internal static async Task<GeminiContent?> Invoke(ILokiClient sdk, bool stream = false, List<GeminiContentEvent>? seen = null, Action<GeminiContentEvent>? observe = null, CancellationToken? token = null)
    {
        var request = Request(); var before = JsonSerializer.Serialize(request); var call = new LlmCallOptions { SessionId = "configured-gemini" }; var cancellation = token ?? TestContext.Current.CancellationToken;
        try { if (!stream) return await sdk.LLM.Gemini.GenerateAsync(request, call, cancellation); await foreach (var item in sdk.LLM.Gemini.StreamAsync(request, call, cancellation)) { observe?.Invoke(item); seen!.Add(item); } return null; }
        finally { Assert.Equal(before, JsonSerializer.Serialize(request)); }
    }
}
