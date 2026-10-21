namespace Asgard.Loki.LLM.OpenAI;

/// <summary>原生 HTTP 操作相对地址与认证位置；完整服务器仍由 LlmTarget 决定，不包含密钥。</summary>
public sealed record OpenAiHttpTransportOptions
{
    /// <summary>相对目标完整根的 Chat 操作路径，可带固定查询，不接受其他服务器或路径模板。</summary>
    public string ChatCompletionsPath { get; init; } = "chat/completions";
    /// <summary>相对目标完整根的 Responses 操作路径。</summary>
    public string ResponsesPath { get; init; } = "responses";
    /// <summary>相对目标完整根的原生压缩操作路径，仍需目标显式授权。</summary>
    public string CompactionPath { get; init; } = "responses/compact";
    /// <summary>相对目标完整根的文本补全操作路径。</summary>
    public string TextCompletionsPath { get; init; } = "completions";
    /// <summary>相对目标完整根的 Moderations 操作路径。</summary>
    public string ModerationsPath { get; init; } = "moderations";
    /// <summary>仅控制 HTTP；WebSocket 和 Realtime 保留各自原生认证契约。</summary>
    public OpenAiHttpAuthentication Authentication { get; init; }
    /// <summary>Header/Query 必须明确提供的头或参数名称；Bearer/None 必须省略。</summary>
    public string? CredentialName { get; init; }
    /// <summary>Header/Query 中密钥前的固定非密钥文本；Bearer/None 必须为空。</summary>
    public string CredentialPrefix { get; init; } = "";
    /// <summary>Header/Query 中密钥后的固定非密钥文本；Bearer/None 必须为空。</summary>
    public string CredentialSuffix { get; init; } = "";
}
