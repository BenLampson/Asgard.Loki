using System.Text.Json;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原生 SiliconFlow Chat 各模式的完整目标、独立样本及实际 SDK 请求。</summary>
internal static class SiliconFlowFixture
{
    internal static JsonElement Window => OpenAiHttpFixture.Golden("siliconflow-compatible");
    internal static JsonElement Part(string mode) => Window.GetProperty("modes").GetProperty(mode);
    internal static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    internal static LlmTarget Target(string mode, int profile = 0) => new() { Id = "siliconflow-target", ProviderId = "siliconflow-native", BaseAddress = new(Profile(profile).GetProperty("root").GetString()!), UpstreamModel = Part(mode).GetProperty("model").GetString()!, CredentialId = "sf-key", Protocols = LlmProtocol.ChatCompletions };
    internal static ILokiClient Build(OpenAiHttpFactory factory, string mode, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null, string client = "Explicit.SiliconFlow.Native", OpenAiHttpOptions? options = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, options ?? new(), id: "siliconflow-native", httpClientName: client)], SessionStore = store ?? CompactionFixture.Store(), Credentials = [new("sf-key", key ?? Profile(profile).GetProperty("key").GetString()!)], Routes = [new() { Model = "public", Targets = [target ?? Target(mode, profile)] }] }).Build();
    internal static ChatCompletionRequest Request(string mode) => Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()!;
    /// <summary>同一个实际请求对象在成功和失败后均不可被 SDK 改写。</summary>
    internal static async Task<ChatCompletion?> Invoke(ILokiClient sdk, string mode, bool stream = false, List<ChatCompletionEvent>? seen = null, Action<ChatCompletionEvent>? observe = null, ChatCompletionRequest? request = null, CancellationToken? cancellationToken = null)
    {
        request ??= Request(mode); var before = JsonSerializer.Serialize(request); var call = new LlmCallOptions { SessionId = "siliconflow" }; var token = cancellationToken ?? TestContext.Current.CancellationToken;
        try
        {
            if (!stream) return await sdk.LLM.ChatCompletions.CompleteAsync(request, call, token);
            await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(request, call, token)) { observe?.Invoke(item); seen!.Add(item); } return null;
        }
        finally { Assert.Equal(before, JsonSerializer.Serialize(request)); }
    }
    internal static bool Ending(ChatCompletionEvent item) => item.Data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null);
    internal static string Frame(JsonElement data) => OpenAiHttpFixture.Frame(JsonSerializer.Serialize(data));
}
