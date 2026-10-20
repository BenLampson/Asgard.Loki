using System.Text.Json;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>独立完整样本和内存双工边界；不创建端口、读取凭据或回退 HTTP。</summary>
internal static class AzureRealtimeFixture
{
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(AzureRealtimeFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.Azure.Tests.Fixtures.realtime.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.GetProperty(name).Clone();
    }
    internal static AzureLlmOptions Options(int profile = 0) => new() { ApiStyle = Enum.Parse<AzureLlmApiStyle>(Part("profiles")[profile].GetProperty("style").GetString()!), ApiVersion = Part("profiles")[profile].GetProperty("version").GetString() };
    internal static LlmTarget Target(int profile = 0) => AzureCompactionFixture.Target() with { BaseAddress = new(Part("profiles")[profile].GetProperty("root").GetString()!), UpstreamModel = "deploy.rt.1", Protocols = LlmProtocol.Realtime, Features = LlmTargetFeatures.None };
    internal static LlmAdapterContext Context(bool external = false, int profile = 0) => new(Target(profile), AzureFixture.Credential(external), "public", "rt-session");
    internal static AzureLlmProvider Provider(AzureCompactionHttpFactory http, AzureRealtimeFactory sockets, int profile = 0, AzureTokenSource? source = null, AzureClock? clock = null, int maximum = 16 * 1024 * 1024)
        => new(http, Options(profile), source, new AzureRealtimeOptions { MaximumMessageBytes = maximum }, sockets, timeProvider: clock);
    internal static ILokiClient Build(AzureLlmProvider provider, bool external = false, int profile = 0, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [credential ?? AzureFixture.Credential(external)], SessionStore = store ?? AzureFixture.WebSocketStore(), TargetSelector = new TestTargetSelector(selected), Routes = [new() { Model = "public", Targets = targets ?? [Target(profile)] }] }).Build();
    internal static LlmCallOptions Call() => new() { SessionId = "rt-session" };
    internal static async Task<List<RealtimeEvent>> Drain(IRealtimeConnection connection, CancellationToken token)
    { List<RealtimeEvent> events = []; await foreach (var item in connection.ReadEventsAsync(token)) events.Add(item); return events; }
}
