using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>独立原生审核样本、完整目标和纯本地 HTTP，不取得真实凭据或访问媒体。</summary>
internal static class ModerationFixture
{
    internal static JsonElement Part(string name) => OpenAiHttpFixture.Golden("moderations").GetProperty(name).Clone();
    internal static JsonElement Element(string json) { using var source = JsonDocument.Parse(json); return source.RootElement.Clone(); }
    internal static ModerationRequest Request(int index = 0) => new()
    { Model = "public-moderation", Parameters = Part("requests")[index].EnumerateObject().Where(p => p.Name != "model").ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal) };
    internal static JsonObject Payload(int index = 0) => JsonNode.Parse(Part("requests")[index].GetRawText())!.AsObject();
    internal static LlmTarget Target(string id = "a", string root = "https://moderation.example.invalid/tenant/v1/") => new()
    { Id = id, ProviderId = "openai", BaseAddress = new(root), UpstreamModel = "caller-selected-moderation-model", CredentialId = "key-" + id, Protocols = LlmProtocol.Moderations };
    internal static LlmAdapterContext Context(OpenAiLlmProvider provider) => new(Target() with { ProviderId = provider.Id }, new("key-a", "fixture-moderation-key-a"), "public-moderation", "session");
    internal static HttpResponseMessage Json(JsonElement raw, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(raw.GetRawText(), Encoding.UTF8, "application/json") };
    internal static ModerationHttpFactory Factory(int index = 0) => new() { OnSend = (_, _) => Task.FromResult(Json(Part("responses")[index])) };
    internal static OpenAiLlmProvider Provider(IHttpClientFactory factory, OpenAiHttpOptions? options = null) => new(factory, options ?? new(), httpClientName: "moderation-tests");
    internal static ILokiClient Build(IHttpClientFactory factory, OpenAiHttpOptions? options = null, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, ILlmTargetSelector? selector = null, OpenAiLlmProvider? provider = null) => new LokiClientBuilder().AddLLM(new()
    {
        Providers = [provider ?? Provider(factory, options)], Credentials = [new("key-a", "fixture-moderation-key-a"), new("key-b", "fixture-moderation-key-b")],
        SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public-moderation", Targets = targets ?? [Target()] }], TargetSelector = selector ?? new RoundRobinLlmTargetSelector()
    }).Build();
    internal static Task<Moderation> Invoke(OpenAiLlmProvider provider, int index, CancellationToken token) => ((IModerationsAdapter)provider).CreateAsync(Payload(index), Context(provider), token);
}
