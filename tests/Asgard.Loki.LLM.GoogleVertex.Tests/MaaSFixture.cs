using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>MaaS 完整原生样本和公开 SDK 装配，不依赖真实云端或登录配置。</summary>
internal static class MaaSFixture
{
    internal const string Model = "deepseek-ai/configured-model";
    internal static JsonObject Request() => JsonNode.Parse(VertexFixture.Text("maas-request"))!.AsObject();
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent(VertexFixture.Text("maas-response"), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(string id = "first") => VertexFixture.Target(id, Model) with { ProviderId = "google-vertex-maas", Protocols = LlmProtocol.ChatCompletions, BaseAddress = new("https://" + id + ".invalid/proxy/v1/projects/project-a/locations/us-central1/endpoints/openapi/") };
    internal static LlmAdapterContext Context(LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target(), credential ?? LlmCredential.External("identity", "principal-a"), "public", "maas-session");
    internal static ILokiClient Client(GoogleVertexMaaSLlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [credential ?? LlmCredential.External("identity", "principal-a")], SessionStore = store ?? VertexFixture.Store(), TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target()] }] }).Build();
    internal static Task<ChatCompletion> Invoke(ILokiClient client) => client.LLM.ChatCompletions.CompleteAsync(Request().Deserialize<ChatCompletionRequest>()!, new() { SessionId = "maas-session" }, TestContext.Current.CancellationToken);
}
