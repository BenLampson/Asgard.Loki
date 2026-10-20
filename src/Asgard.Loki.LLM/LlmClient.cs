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
        Realtime = new RealtimeClient(invocation);
        ClaudeMessages = new ClaudeMessagesClient(invocation);
        Gemini = new GeminiClient(invocation);
        OllamaChat = new OllamaChatClient(invocation);
        OllamaGenerate = new OllamaGenerateClient(invocation);
        BedrockConverse = new BedrockConverseClient(invocation);
        BedrockNova = new BedrockNovaClient(invocation);
        Moderations = new ModerationsClient(invocation);
        TextCompletions = new TextCompletionsClient(invocation);
        Hunyuan = new HunyuanClient(invocation);
    }

    /// <summary>访问 Chat Completions 能力。</summary>
    public IChatCompletionsClient ChatCompletions { get; }
    /// <summary>访问 Responses 能力。</summary>
    public IResponsesClient Responses { get; }
    /// <summary>访问独立的原生 Realtime 双工协议。</summary>
    public IRealtimeClient Realtime { get; }
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
    /// <summary>访问原生 Moderations 模型接口，输入/分类结果不成为应用内容策略。</summary>
    public IModerationsClient Moderations { get; }
    /// <summary>访问原生文本补全，不将 prompt/token 输入转换成 Chat 消息。</summary>
    public ITextCompletionsClient TextCompletions { get; }
    /// <summary>访问混元原生 PascalCase JSON/SSE，不隐式转换工具或消息。</summary>
    public IHunyuanClient Hunyuan { get; }

}
