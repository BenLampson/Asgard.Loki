using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整独立原生报文与公开 SDK 装配，缓存/协调均为离线测试实现。</summary>
internal static class AwsFixture
{
    internal const string Model = "anthropic.configured-model-v1:0";
    internal static string Text(string name) { using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.AWS.Tests.Fixtures." + name + ".json")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    internal static JsonObject Request() => JsonNode.Parse(Text("request"))!.AsObject();
    internal static HttpResponseMessage Reply() => new(HttpStatusCode.OK) { Content = new StringContent(Text("response"), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(string id = "first") => new() { Id = id, ProviderId = "aws", BaseAddress = new("https://" + id + ".invalid/proxy/"), UpstreamModel = Model, CredentialId = "identity", Protocols = LlmProtocol.ClaudeMessages };
    internal static LlmAdapterContext Context(LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target(), credential ?? LlmCredential.External("identity", "principal-a"), "public", "aws-session");
    internal static ControlledSessionStore Store() => new(new DistributedCacheLlmSessionStore(new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())), new TestSessionLock()));
    internal static ILokiClient Client(AwsLlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [credential ?? LlmCredential.External("identity", "principal-a")], SessionStore = store ?? Store(), TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target()] }] }).Build();
    internal static Task<ClaudeMessage> Invoke(ILokiClient client) => client.LLM.ClaudeMessages.CreateAsync(Request().Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken);
}
