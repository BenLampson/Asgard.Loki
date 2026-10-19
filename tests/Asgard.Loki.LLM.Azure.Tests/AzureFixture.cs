using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>独立完整请求/响应金样与公开 SDK 装配；所有地址均由替代 HTTP 拦截。</summary>
internal static class AzureFixture
{
    internal static string Text(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Azure.Tests.Fixtures." + name + ".json")!; using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static JsonObject Request(bool responses) => JsonNode.Parse(Text(responses ? "responses-request" : "chat-request"))!.AsObject();
    internal static string Response(bool responses) => Text(responses ? "responses-response" : "chat-response");
    internal static string Sse(bool responses)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Azure.Tests.Fixtures." + (responses ? "responses" : "chat") + ".sse")!; using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static HttpResponseMessage Reply(bool responses) => new(HttpStatusCode.OK) { Content = new StringContent(Response(responses), Encoding.UTF8, "application/json") };
    internal static LlmTarget Target(bool responses, AzureLlmApiStyle style, string id = "primary") => new()
    { Id = id, ProviderId = "azure", BaseAddress = new("https://" + id + ".invalid/proxy/openai/" + (style == AzureLlmApiStyle.V1 ? "v1/" : "")), UpstreamModel = "deploy.1", CredentialId = "identity", Protocols = responses ? LlmProtocol.Responses : LlmProtocol.ChatCompletions };
    internal static LlmCredential Credential(bool external, string principal = "tenant/principal") => external ? LlmCredential.External("identity", principal) : new("identity", "fixture-key");
    internal static LlmAdapterContext Context(bool responses, AzureLlmApiStyle style = AzureLlmApiStyle.V1, bool external = false) => new(Target(responses, style), Credential(external), "public", "azure-session");
    internal static ILokiClient Client(AzureLlmProvider provider, bool responses, AzureLlmApiStyle style = AzureLlmApiStyle.V1, bool external = false,
        ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, string principal = "tenant/principal")
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [Credential(external, principal)], SessionStore = store ?? WebSocketStore(), TargetSelector = new TestTargetSelector(selected),
            Routes = [new() { Model = "public", Targets = targets ?? [Target(responses, style)] }] }).Build();
    internal static ControlledSessionStore WebSocketStore() => new(new DistributedCacheLlmSessionStore(new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
        Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())), new TestSessionLock()));
    /// <summary>正常金样可反序列化；异常 Unicode 测试会直接构造原生 DOM。</summary>
    internal static async Task<LlmJsonResult> Invoke(ILokiClient client, bool responses, JsonObject? request = null)
    {
        request ??= Request(responses); var call = new LlmCallOptions { SessionId = "azure-session" }; var token = TestContext.Current.CancellationToken;
        return responses ? await client.LLM.Responses.CreateAsync(request.Deserialize<ResponseRequest>()!, call, token) : await client.LLM.ChatCompletions.CompleteAsync(request.Deserialize<ChatCompletionRequest>()!, call, token);
    }
}
