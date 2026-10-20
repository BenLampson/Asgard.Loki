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
    OllamaChat = 16,
    /// <summary>Ollama 原生 api/generate JSON 与 NDJSON 协议。</summary>
    OllamaGenerate = 32,
    /// <summary>Amazon Bedrock Converse 原生协议，不等同于 Claude Messages。</summary>
    BedrockConverse = 64,
    /// <summary>Amazon Nova 理解模型的 InvokeModel 原生协议，不等同于 Converse。</summary>
    BedrockNova = 128,
    /// <summary>原生 Realtime JSON 双工协议，独立于 Responses WebSocket。</summary>
    Realtime = 256,
    /// <summary>原生 Moderations 完整 JSON 模型调用，不执行应用内容处置策略。</summary>
    Moderations = 512,
    /// <summary>原生文本补全 JSON/SSE 协议，使用 prompt 而非 Chat messages。</summary>
    TextCompletions = 1024,
    /// <summary>腾讯混元原生 PascalCase ChatCompletions JSON/SSE 协议。</summary>
    Hunyuan = 2048,
    /// <summary>讯飞星火原生 WebSocket JSON 消息协议，独立于 Chat/SSE。</summary>
    Spark = 4096,
    /// <summary>Cohere v1 Chat 原生 JSON/NDJSON 协议，独立于 OpenAI Chat。</summary>
    CohereChat = 8192
}
