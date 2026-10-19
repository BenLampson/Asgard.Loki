using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>组织各个原生 LLM 协议的独立公共入口。</summary>
public sealed class LlmClient
{
    /// <summary>基于已经校验的不可变配置快照创建能力客户端。</summary>
    internal LlmClient(LlmConfiguration configuration)
    {
        var invocation = new LlmInvocation(configuration);
        ChatCompletions = new ChatCompletionsClient(invocation);
        Responses = new ResponsesClient(invocation);
        ClaudeMessages = new ClaudeMessagesClient(invocation);
        Gemini = new GeminiClient(invocation);
        OllamaChat = new OllamaChatClient(invocation);
        OllamaGenerate = new OllamaGenerateClient(invocation);
        BedrockConverse = new BedrockConverseClient(invocation);
        BedrockNova = new BedrockNovaClient(invocation);
    }

    /// <summary>访问 Chat Completions 能力。</summary>
    public IChatCompletionsClient ChatCompletions { get; }
    /// <summary>访问 Responses 能力。</summary>
    public IResponsesClient Responses { get; }
    /// <summary>访问 Claude Messages 原生能力。</summary>
    public IClaudeMessagesClient ClaudeMessages { get; }
    /// <summary>访问 Gemini 原生能力。</summary>
    public IGeminiClient Gemini { get; }
    /// <summary>访问 Ollama 原生 api/chat JSON 与 NDJSON 能力。</summary>
    public IOllamaChatClient OllamaChat { get; }
    /// <summary>访问 Ollama 原生 api/generate，不自动管理 prompt/context。</summary>
    public IOllamaGenerateClient OllamaGenerate { get; }
    /// <summary>访问 Bedrock Converse 原生协议，不隐式转换成 Claude 或 Chat。</summary>
    public IBedrockConverseClient BedrockConverse { get; }
    /// <summary>访问 Nova 理解模型的 Invoke 原生协议，不隐式选择模型或转换到 Chat。</summary>
    public IBedrockNovaClient BedrockNova { get; }
}
