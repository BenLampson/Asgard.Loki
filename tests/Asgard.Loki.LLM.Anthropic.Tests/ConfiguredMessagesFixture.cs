using System.Text.Json;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>独立完整原生 Messages 样本及实际 SDK 调用构造；不生成预期正文。</summary>
internal static class ConfiguredMessagesFixture
{
    internal static JsonElement Window => AnthropicHttpFixture.Golden("configured-http");
    internal static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    internal static ClaudeMessageRequest Request() => Window.GetProperty("caller").Deserialize<ClaudeMessageRequest>()!;
    internal static AnthropicHttpOptions Options(int profile) => new() { Authentication = Enum.Parse<AnthropicAuthentication>(Profile(profile).GetProperty("authentication").GetString()!), Transport = new() { MessagesPath = Window.GetProperty("path").GetString()!, CredentialName = Profile(profile).GetProperty("name").GetString(), CredentialPrefix = Profile(profile).GetProperty("prefix").GetString()!, CredentialSuffix = Profile(profile).GetProperty("suffix").GetString()! } };
    internal static AnthropicLlmProvider Provider(AnthropicHttpFactory factory, int profile, AnthropicHttpOptions? options = null, string client = "Explicit.Messages", string version = "2023-06-01") => new(factory, options ?? Options(profile), "configured-messages", client, version);
    internal static LlmTarget Target(ILlmProvider provider) => AnthropicHttpFixture.Target(provider, Window.GetProperty("root").GetString()!, Window.GetProperty("model").GetString()!) with { Version = "configured-v1" };
    internal static ILokiClient Build(AnthropicLlmProvider provider, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, LlmCredential? credential = null)
        => new LokiClientBuilder().AddLLM(new() { Providers = [provider], SessionStore = store ?? AnthropicHttpFixture.Store(), Credentials = [credential ?? (profile == 2 ? LlmCredential.Anonymous("key") : new("key", Profile(profile).GetProperty("key").GetString()!))], Routes = [new() { Model = "public", Targets = [target ?? Target(provider)] }] }).Build();
    internal static bool Ending(ClaudeMessageEvent item) => item.Type is "message_delta" or "message_stop" or "error";
    internal static async Task<ClaudeMessage?> Invoke(ILokiClient sdk, bool stream = false, List<ClaudeMessageEvent>? seen = null, Action<ClaudeMessageEvent>? observe = null, CancellationToken? token = null)
    {
        var request = Request(); var before = JsonSerializer.Serialize(request); var call = new LlmCallOptions { SessionId = "configured-messages" }; var actualToken = token ?? TestContext.Current.CancellationToken;
        try
        {
            if (!stream) return await sdk.LLM.ClaudeMessages.CreateAsync(request, call, actualToken);
            await foreach (var item in sdk.LLM.ClaudeMessages.StreamAsync(request, call, actualToken)) { observe?.Invoke(item); seen!.Add(item); } return null;
        }
        finally { Assert.Equal(before, JsonSerializer.Serialize(request)); }
    }
}
