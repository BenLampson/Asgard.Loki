using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>跨协议使用同一公开配置/调用入口，缓存仍使用真实 MemoryDistributedCache 与显式协调器。</summary>
internal static class OutputFormatFixture
{
    /// <summary>按协议配置格式限制，不根据测试目标域名或模型名推断支持。</summary>
    internal static LlmTarget Target(LlmProtocol protocol, IReadOnlyList<string>? allowed = null) => new()
    {
        Id = "format-target", ProviderId = "formats", BaseAddress = new("https://format.invalid/"), UpstreamModel = "actual", CredentialId = "key", Protocols = protocol,
        AllowedOutputFormats = allowed is null ? null : new Dictionary<LlmProtocol, IReadOnlyList<string>> { [protocol] = allowed }
    };
    /// <summary>允许调用方保留集合引用，以验证注册时深快照。</summary>
    internal static LlmOptions Options(OutputFormatProvider provider, ILlmSessionStore store, params LlmTarget[] targets) => new()
    {
        Providers = [provider], SessionStore = store, Credentials = [new("key", "test-only")], TargetSelector = new TestTargetSelector(0),
        Routes = [new() { Model = "public", Targets = targets }]
    };
    /// <summary>跨全部已声明支持的格式外壳调用实际公共客户端，流式必须枚举到结束。</summary>
    internal static async Task Invoke(ILokiClient client, LlmProtocol protocol, bool stream, string json, string session = "formats")
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var call = new LlmCallOptions { SessionId = session }; var token = TestContext.Current.CancellationToken;
        switch (protocol)
        {
            case LlmProtocol.ChatCompletions:
                var chat = new ChatCompletionRequest { Model = "public", Messages = [], Parameters = parameters };
                if (stream) await Drain(client.LLM.ChatCompletions.StreamAsync(chat, call, token)); else await client.LLM.ChatCompletions.CompleteAsync(chat, call, token); break;
            case LlmProtocol.Responses:
                var response = new ResponseRequest { Model = "public", Input = JsonValue.Create("JSON please"), Parameters = parameters };
                if (stream) await Drain(client.LLM.Responses.StreamAsync(response, call, token)); else await client.LLM.Responses.CreateAsync(response, call, token); break;
            case LlmProtocol.ClaudeMessages:
                var claude = new ClaudeMessageRequest { Model = "public", Messages = [], MaxTokens = 100, Parameters = parameters };
                if (stream) await Drain(client.LLM.ClaudeMessages.StreamAsync(claude, call, token)); else await client.LLM.ClaudeMessages.CreateAsync(claude, call, token); break;
            case LlmProtocol.Gemini:
                var gemini = new GeminiContentRequest { Model = "public", Contents = [], Parameters = parameters };
                if (stream) await Drain(client.LLM.Gemini.StreamAsync(gemini, call, token)); else await client.LLM.Gemini.GenerateAsync(gemini, call, token); break;
            case LlmProtocol.OllamaChat:
                var ollamaChat = new OllamaChatRequest { Model = "public", Messages = [], Parameters = parameters };
                if (stream) await Drain(client.LLM.OllamaChat.StreamAsync(ollamaChat, call, token)); else await client.LLM.OllamaChat.CompleteAsync(ollamaChat, call, token); break;
            case LlmProtocol.OllamaGenerate:
                var ollama = new OllamaGenerateRequest { Model = "public", Parameters = parameters };
                if (stream) await Drain(client.LLM.OllamaGenerate.StreamAsync(ollama, call, token)); else await client.LLM.OllamaGenerate.GenerateAsync(ollama, call, token); break;
            case LlmProtocol.BedrockConverse:
                var bedrock = new BedrockConverseRequest { Model = "public", Parameters = parameters };
                if (stream) await Drain(client.LLM.BedrockConverse.StreamAsync(bedrock, call, token)); else await client.LLM.BedrockConverse.ConverseAsync(bedrock, call, token); break;
            case LlmProtocol.CohereChat:
                var cohere = new CohereChatRequest { Model = "public", Parameters = parameters };
                if (stream) await Drain(client.LLM.CohereChat.StreamAsync(cohere, call, token)); else await client.LLM.CohereChat.CompleteAsync(cohere, call, token); break;
            case LlmProtocol.WorkersAiText:
                var workers = new WorkersAiTextRequest { Model = "public", Parameters = parameters };
                if (stream) await Drain(client.LLM.WorkersAiText.StreamAsync(workers, call, token)); else await client.LLM.WorkersAiText.CompleteAsync(workers, call, token); break;
            default: throw new NotSupportedException();
        }
    }
    /// <summary>确保异步流校验真正执行。</summary>
    private static async Task Drain<T>(IAsyncEnumerable<T> events) { await foreach (var _ in events) { } }
}
