namespace Asgard.Loki.LLM;

/// <summary>声明目标已配置支持的 LLM 协议。</summary>
[Flags]
public enum LlmProtocol
{
    /// <summary>不支持任何协议。</summary>
    None = 0,
    /// <summary>Chat Completions 协议。</summary>
    ChatCompletions = 1,
    /// <summary>Responses 协议。</summary>
    Responses = 2,
    /// <summary>Anthropic Claude Messages 原生协议。</summary>
    ClaudeMessages = 4,
    /// <summary>Google Gemini generateContent 原生协议。</summary>
    Gemini = 8,
    /// <summary>Ollama 原生 api/chat JSON 与 NDJSON 协议，不等同于 OpenAI Chat。</summary>
    OllamaChat = 16
}
