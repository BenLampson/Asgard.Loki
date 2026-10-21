using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>Express API Key/短资源的显式 SDK 装配，不构造或借用外部令牌源。</summary>
internal static class ExpressFixture
{
    internal const string Resource = "publishers/google/models/configured-express";
    internal const string Key = "fixture-express-key";
    internal static JsonObject Request() => JsonNode.Parse(VertexFixture.Text("express-request"))!.AsObject();
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent(VertexFixture.Text("express-response"), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(string id = "first", string resource = Resource) => VertexFixture.Target(id, resource) with { ProviderId = "google-vertex-express" };
    internal static LlmAdapterContext Context(LlmCredential? credential = null) => new(Target(), credential ?? new("identity", Key), "public", "express-session");
    internal static ILokiClient Client(GoogleVertexExpressLlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [credential ?? new("identity", Key)], SessionStore = store ?? VertexFixture.Store(), TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target()] }] }).Build();
    internal static async Task Invoke(ILokiClient client, bool stream)
    {
        var input = Request().Deserialize<GeminiContentRequest>()!; var call = new LlmCallOptions { SessionId = "express-session" }; var token = TestContext.Current.CancellationToken;
        if (stream) { await foreach (var _ in client.LLM.Gemini.StreamAsync(input, call, token)) { } } else await client.LLM.Gemini.GenerateAsync(input, call, token);
    }
    internal static async Task Direct(GoogleVertexExpressLlmProvider provider, bool stream, CancellationToken token)
    {
        if (stream) { await foreach (var _ in provider.StreamAsync(Request(), Context(), token)) { } } else await provider.GenerateAsync(Request(), Context(), token);
    }
}
