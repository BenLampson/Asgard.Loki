using System.Text.Json;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>显式原生 xAI 完整目标、原始协议样本和实际公共 SDK 调用。</summary>
internal static class XaiFixture
{
    internal static JsonElement Window => OpenAiHttpFixture.Golden("xai-compatible");
    internal static JsonElement Part(string mode) => Window.GetProperty(mode);
    internal static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    internal static LlmTarget Target(string mode, int profile = 0) => new() { Id = "xai-target", ProviderId = "xai-native", BaseAddress = new(Profile(profile).GetProperty("root").GetString()!), UpstreamModel = Part(mode).GetProperty("model").GetString()!, CredentialId = "xai-key", Protocols = mode == "chat" ? LlmProtocol.ChatCompletions : LlmProtocol.Responses };
    internal static ILokiClient Build(OpenAiHttpFactory factory, string mode, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null, string client = "Explicit.XAI.Native", OpenAiHttpOptions? options = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, options ?? new(), id: "xai-native", httpClientName: client)], SessionStore = store ?? CompactionFixture.Store(), Credentials = [new("xai-key", key ?? Profile(profile).GetProperty("key").GetString()!)], Routes = [new() { Model = "public", Targets = [target ?? Target(mode, profile)] }] }).Build();
    internal static object Request(string mode) => mode == "chat" ? Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()! : Part(mode).GetProperty("caller").Deserialize<ResponseRequest>()!;
    /// <summary>实际发送的请求对象逐次核对调用前后内容，流式也不另造断言对象。</summary>
    internal static async Task<LlmJsonResult?> Invoke(ILokiClient sdk, string mode, bool stream = false, List<LlmJsonEvent>? seen = null, Action<LlmJsonEvent>? observe = null, object? request = null, CancellationToken? cancellationToken = null)
    {
        request ??= Request(mode); var before = JsonSerializer.Serialize(request); var call = new LlmCallOptions { SessionId = "xai" }; var token = cancellationToken ?? TestContext.Current.CancellationToken; LlmJsonResult? result = null;
        void Add(LlmJsonEvent item) { observe?.Invoke(item); seen!.Add(item); }
        try
        {
            if (mode == "chat") { if (stream) await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)request, call, token)) Add(item); else result = await sdk.LLM.ChatCompletions.CompleteAsync((ChatCompletionRequest)request, call, token); }
            else { if (stream) await foreach (var item in sdk.LLM.Responses.StreamAsync((ResponseRequest)request, call, token)) Add(item); else result = await sdk.LLM.Responses.CreateAsync((ResponseRequest)request, call, token); }
            return result;
        }
        finally { Assert.Equal(before, JsonSerializer.Serialize(request)); }
    }
    internal static bool Ending(LlmJsonEvent item) => item.Type is "response.completed" or "response.failed" or "response.incomplete" or "error" || item.Data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null);
    internal static string Frame(JsonElement data) => OpenAiHttpFixture.Frame(JsonSerializer.Serialize(data));
}
