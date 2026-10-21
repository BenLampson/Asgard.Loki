using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>独立完整原生压缩窗口和固定目标，所有缓存/认证/HTTP 均为本地替身。</summary>
internal static class AzureCompactionFixture
{
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(AzureCompactionFixture).Assembly.GetManifestResourceStream(typeof(AzureCompactionFixture).Namespace + ".Fixtures.compaction.json")!;
        using var source = JsonDocument.Parse(stream); return source.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string json) { using var source = JsonDocument.Parse(json); return source.RootElement.Clone(); }
    internal static JsonObject Payload(int index = 0) => JsonNode.Parse(Part("requests")[index].GetRawText())!.AsObject();
    internal static ResponseCompactionRequest Request(int index = 0) => Payload(index).Deserialize<ResponseCompactionRequest>()! with { Model = "public" };
    internal static AzureLlmOptions Options(int profile = 0, int? requestMaximum = null, int responseMaximum = 32 * 1024 * 1024)
    {
        var raw = Part("profiles")[profile]; return new()
        { ApiStyle = Enum.Parse<AzureLlmApiStyle>(raw.GetProperty("style").GetString()!), ApiVersion = raw.GetProperty("version").GetString(), MaximumCompactionRequestBytes = requestMaximum, MaximumBodyBytes = responseMaximum };
    }
    internal static LlmTarget Target(int profile = 0, LlmTargetFeatures features = LlmTargetFeatures.ResponsesCompaction) => new()
    { Id = "primary", ProviderId = "azure", BaseAddress = new(Part("profiles")[profile].GetProperty("root").GetString()!), UpstreamModel = "deploy.compact.1", CredentialId = "identity", Protocols = LlmProtocol.Responses, Features = features };
    internal static LlmAdapterContext Context(bool external = false, int profile = 0) => new(Target(profile), AzureFixture.Credential(external), "public", "compact-session");
    internal static ILokiClient Build(AzureLlmProvider provider, bool external = false, int profile = 0, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null) => new LokiClientBuilder().AddLLM(new()
    { Providers = [provider], Credentials = [AzureFixture.Credential(external)], SessionStore = store ?? AzureFixture.WebSocketStore(), Routes = [new() { Model = "public", Targets = targets ?? [Target(profile)] }] }).Build();
    internal static HttpResponseMessage Json(JsonElement raw, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(raw.GetRawText(), Encoding.UTF8, "application/json") };
    internal static AzureCompactionHttpFactory Factory() => new() { OnSend = (_, _) => Task.FromResult(Json(Part("response"))) };
    internal static Task<ResponseCompaction> Invoke(AzureLlmProvider provider, CancellationToken token) => provider.CompactAsync(Payload(), Context(), token);
    internal static LlmCallOptions Call(string id = "compact-session") => new() { SessionId = id };
    internal static async Task SeedAsync(ILokiClient client, CancellationToken token)
        => _ = await client.LLM.Responses.CreateAsync(new() { Model = "public", Input = "seed" }, Call(), token);
    internal static HttpResponseMessage SeedReply() => Json(Element("""{"id":"resp-owned-fixture","status":"completed","output":[],"future":false}"""));
}
