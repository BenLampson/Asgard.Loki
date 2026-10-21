using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立完整 Converse JSON/HTTP 样本，不根据模型品牌选择协议。</summary>
internal static class ConverseFixture
{
    internal const string Model = "amazon.configured-converse-v1:0";
    internal static JsonObject Request() => JsonNode.Parse(AwsFixture.Text("converse-request"))!.AsObject();
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent(AwsFixture.Text("converse-response"), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(string id = "first") => AwsFixture.Target(id) with { Protocols = LlmProtocol.BedrockConverse, UpstreamModel = Model };
    internal static ILokiClient Client(AwsLlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, LlmCredential? credential = null) => AwsFixture.Client(provider, store, targets ?? [Target()], selected, credential);
    internal static LlmAdapterContext Context(LlmTarget? target = null, LlmCredential? credential = null) => AwsFixture.Context(target ?? Target(), credential);
    internal static Task<BedrockConverseResponse> Invoke(ILokiClient client) => client.LLM.BedrockConverse.ConverseAsync(Request().Deserialize<BedrockConverseRequest>()!, new() { SessionId = "converse-session" }, TestContext.Current.CancellationToken);
}
