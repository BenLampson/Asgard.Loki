using System.Text.Json;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>明确声明兼容原生服务、完整目标与原始协议，不推导品牌能力。</summary>
internal static class InferencePresetFixture
{
    internal static JsonElement Window => OpenAiHttpFixture.Golden("inference-presets");
    internal static JsonElement Part(string mode) => Window.GetProperty("modes").GetProperty(mode);
    internal static JsonElement Profile(string mode, int profile) => Window.GetProperty("profiles")[profile];
    internal static string Protocol(string mode) => mode;
    internal static string Family(string mode) => "inference-preset";
    internal static IEnumerable<string> Modes => Window.GetProperty("modes").EnumerateObject().Select(p => p.Name);
    internal static LlmTarget Target(string mode, int profile = 0) => new() { Id = "inference-preset-target", ProviderId = Family(mode)+"-native", BaseAddress = new(Profile(mode, profile).GetProperty("root").GetString()!), UpstreamModel = Part(mode).GetProperty("model").GetString()!, CredentialId = "selected-key", Version = Profile(mode, profile).GetProperty("version").GetString()!, Protocols = Protocol(mode) switch { "text" => LlmProtocol.TextCompletions, "responses" => LlmProtocol.Responses, _ => LlmProtocol.ChatCompletions } };
    internal static ILokiClient Build(OpenAiHttpFactory factory, string mode, int profile = 0, ILlmSessionStore? store = null, LlmTarget? target = null, string? key = null, string? client = null, OpenAiHttpOptions? options = null)
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [new OpenAiLlmProvider(factory, options ?? new(), id: Family(mode)+"-native", httpClientName: client ?? Profile(mode, profile).GetProperty("client").GetString()!)], SessionStore = store ?? CompactionFixture.Store(), Credentials = [new("selected-key", key ?? Profile(mode, profile).GetProperty("key").GetString()!)], Routes = [new() { Model = "public", Targets = [target ?? Target(mode, profile)] }] }).Build();
    internal static OpenAiHttpFactory Factory(HttpMessageHandler handler, string mode, int profile = 0) => new(handler);
    internal static object Request(string mode) => Protocol(mode) switch { "text" => Part(mode).GetProperty("caller").Deserialize<TextCompletionRequest>()!, "responses" => Part(mode).GetProperty("caller").Deserialize<ResponseRequest>()!, _ => Part(mode).GetProperty("caller").Deserialize<ChatCompletionRequest>()! };
    /// <summary>同一实际调用对象在全部成功与失败路径保持不变。</summary>
    internal static async Task<LlmJsonResult?> Invoke(ILokiClient sdk, string mode, bool stream = false, List<LlmJsonEvent>? seen = null, Action<LlmJsonEvent>? observe = null, object? request = null, CancellationToken? cancellationToken = null)
    {
        request ??= Request(mode); var before = JsonSerializer.Serialize(request); var call = new LlmCallOptions { SessionId = "inference-preset" }; var token = cancellationToken ?? TestContext.Current.CancellationToken; LlmJsonResult? result = null;
        void Add(LlmJsonEvent item) { observe?.Invoke(item); seen!.Add(item); }
        try
        {
            switch (Protocol(mode))
            {
                case "text": if (stream) { await foreach (var item in sdk.LLM.TextCompletions.StreamAsync((TextCompletionRequest)request, call, token)) Add(item); } else result = await sdk.LLM.TextCompletions.CompleteAsync((TextCompletionRequest)request, call, token); break;
                case "responses": if (stream) { await foreach (var item in sdk.LLM.Responses.StreamAsync((ResponseRequest)request, call, token)) Add(item); } else result = await sdk.LLM.Responses.CreateAsync((ResponseRequest)request, call, token); break;
                default: if (stream) { await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync((ChatCompletionRequest)request, call, token)) Add(item); } else result = await sdk.LLM.ChatCompletions.CompleteAsync((ChatCompletionRequest)request, call, token); break;
            }
            return result;
        }
        finally { Assert.Equal(before, JsonSerializer.Serialize(request)); }
    }
    internal static bool Ending(LlmJsonEvent item) => item.Type is "response.completed" or "response.failed" or "response.incomplete" or "error" || item.Data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var f) && f.ValueKind != JsonValueKind.Null);
    internal static string Frame(JsonElement data) => OpenAiHttpFixture.Frame(JsonSerializer.Serialize(data));
}
