using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>独立原生文本补全报文与 SDK 装配，HTTP/缓存/认证均为离线替身。</summary>
internal static class AzureTextFixture
{
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(AzureTextFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.Azure.Tests.Fixtures.text-completions.json")!; using var source = JsonDocument.Parse(stream); return source.RootElement.GetProperty(name).Clone();
    }
    internal static string Sse()
    { using var stream = typeof(AzureTextFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.Azure.Tests.Fixtures.text-completions.sse")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    internal static AzureLlmOptions Options(int profile = 0) => new() { ApiStyle = Enum.Parse<AzureLlmApiStyle>(Part("profiles")[profile].GetProperty("style").GetString()!), ApiVersion = Part("profiles")[profile].GetProperty("version").GetString() };
    internal static LlmTarget Target(int profile = 0) => AzureCompactionFixture.Target() with { BaseAddress = new(Part("profiles")[profile].GetProperty("root").GetString()!), UpstreamModel = "deploy.text.1", Protocols = LlmProtocol.TextCompletions, Features = LlmTargetFeatures.None };
    internal static LlmAdapterContext Context(bool external = false, int profile = 0) => new(Target(profile), AzureFixture.Credential(external), "public", "text-session");
    internal static JsonObject Payload(bool stream = false) => JsonNode.Parse(Part(stream ? "caller_stream" : "caller").GetRawText())!.AsObject();
    internal static TextCompletionRequest Request(bool stream = false) => Payload(stream).Deserialize<TextCompletionRequest>()!;
    internal static ILokiClient Build(AzureLlmProvider provider, bool external = false, int profile = 0, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null) => AzureFixture.Client(provider, false, external: external, store: store, targets: targets ?? [Target(profile)]);
    internal static LlmCallOptions Call() => new() { SessionId = "text-session" };
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value.GetRawText(), Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Streaming(Stream stream) { var content = new StreamContent(stream); content.Headers.ContentType = new("text/event-stream") { CharSet = "utf-8" }; return new(HttpStatusCode.OK) { Content = content }; }
    internal static AzureCompactionHttpFactory Factory() => new() { OnSend = (_, _) => Task.FromResult(Json(Part("response"))) };
    internal static Task<TextCompletion> Invoke(AzureLlmProvider provider, CancellationToken token) => ((ITextCompletionsAdapter)provider).CompleteAsync(Payload(), Context(), token);
    internal static async Task<List<TextCompletionEvent>> Drain(IAsyncEnumerable<TextCompletionEvent> source, CancellationToken token)
    { List<TextCompletionEvent> events = []; await foreach (var item in source.WithCancellation(token)) events.Add(item); return events; }
}
