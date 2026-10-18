using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>组织独立的 Chat Completions 与 Responses 公共接口。</summary>
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
    }

    /// <summary>访问 Chat Completions 能力。</summary>
    public IChatCompletionsClient ChatCompletions { get; }
    /// <summary>访问 Responses 能力。</summary>
    public IResponsesClient Responses { get; }
    /// <summary>访问 Claude Messages 原生能力。</summary>
    public IClaudeMessagesClient ClaudeMessages { get; }
    /// <summary>访问 Gemini 原生能力。</summary>
    public IGeminiClient Gemini { get; }
}
