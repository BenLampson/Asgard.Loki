using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>独立 Claude 请求/最终 wire/原生结果，不借 Gemini 结构生成预期。</summary>
internal static class ClaudeFixture
{
    internal const string Resource = "projects/project-a/locations/us-east5/publishers/anthropic/models/configured-claude@20261019";
    internal static JsonObject Request() => JsonNode.Parse(VertexFixture.Text("claude-request"))!.AsObject();
    internal static string Sse()
    {
        using var stream = typeof(ClaudeFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.GoogleVertex.Tests.Fixtures.claude.sse")!; using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent(VertexFixture.Text("claude-response"), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(string id = "first", string resource = Resource) => VertexFixture.Target(id, resource) with { Protocols = LlmProtocol.ClaudeMessages };
    internal static LlmAdapterContext Context() => new(Target(), LlmCredential.External("identity", "principal-a"), "public", "vertex-claude-session");
    internal static ILokiClient Client(GoogleVertexLlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, string principal = "principal-a")
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [LlmCredential.External("identity", principal)], SessionStore = store ?? VertexFixture.Store(), TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target()] }] }).Build();
    internal static Task<ClaudeMessage> Invoke(ILokiClient client) => client.LLM.ClaudeMessages.CreateAsync(Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "vertex-claude-session" }, TestContext.Current.CancellationToken);
}
