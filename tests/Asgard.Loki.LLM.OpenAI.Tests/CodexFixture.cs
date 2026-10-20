using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>完整独立字面协议与显式完整目标，只有 Session 用例才使用内存分布式缓存和协调器。</summary>
internal static class CodexFixture
{
    internal const string Token = "fixture-codex-bearer";
    internal static DateTimeOffset Now => new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    internal static string Text(string name)
    {
        using var stream = typeof(CodexFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.OpenAI.Tests.Fixtures.Codex." + name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static JsonElement Part(string name) { using var d = JsonDocument.Parse(Text("calls.json")); return d.RootElement.GetProperty(name).Clone(); }
    internal static JsonObject Payload(bool compact = false) => JsonNode.Parse(Part(compact ? "compact_request" : "response_request").GetRawText())!.AsObject();
    internal static CodexLlmOptions Options() => new() { Account = new() { AccountId = Part("account_id").GetString()!, Beta = "responses=experimental", Originator = "loki-fixture" } };
    internal static LlmCredential Credential() => LlmCredential.External("external-a", Part("principal").GetString()!);
    internal static LlmTarget Target() => new() { Id = "codex-a", ProviderId = "codex", BaseAddress = new(Part("root").GetString()!), UpstreamModel = "actual-model", CredentialId = "external-a", Protocols = LlmProtocol.Responses, Features = LlmTargetFeatures.ResponsesCompaction };
    internal static LlmAdapterContext Context(CodexLlmProvider provider, LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target() with { ProviderId = provider.Id }, credential ?? Credential(), "public-route", "codex-session");
    internal static CodexLlmProvider Provider(IHttpClientFactory factory, CodexFixtureSource source, CodexLlmOptions? options = null, CodexTestClock? clock = null, string clientName = "codex-test") => new(factory, options ?? Options(), source, httpClientName: clientName, timeProvider: clock ?? new());
    internal static async Task<JsonElement> Invoke(CodexLlmProvider provider, bool compact, CancellationToken token, JsonObject? payload = null, LlmAdapterContext? context = null)
        => compact ? (await provider.CompactAsync(payload ?? Payload(true), context ?? Context(provider), token)).Data : (await provider.CreateAsync(payload ?? Payload(), context ?? Context(provider), token)).Data;
    internal static async Task<List<ResponseEvent>> Stream(CodexLlmProvider provider, CancellationToken token, Action<ResponseEvent>? observe = null)
    { List<ResponseEvent> output = []; await foreach (var item in provider.StreamAsync(Payload(), Context(provider), token)) { observe?.Invoke(item); output.Add(item); } return output; }
    internal static ILokiClient Client(CodexLlmProvider provider, ILlmSessionStore? store = null, LlmTarget? target = null, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [credential ?? Credential()], SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public-route", Targets = [target ?? Target()] }] }).Build();
    internal static byte[] Bytes(string text) => new UTF8Encoding(false, true).GetBytes(text);
}
