namespace Asgard.Loki.LLM.Google;

/// <summary>原生 Gemini 操作路径和凭据格式；服务器与实际模型仍属于完整目标。</summary>
public sealed record GoogleHttpTransportOptions
{
    /// <summary>完整生成操作相对路径；普通模式必须有且仅有一个路径位置的 {model}。</summary>
    public string GenerateContentPath { get; init; } = "models/{model}:generateContent";
    /// <summary>原生 SSE 操作相对路径，包含调用者明确的固定查询。</summary>
    public string StreamGenerateContentPath { get; init; } = "models/{model}:streamGenerateContent?alt=sse";
    /// <summary>可选固定模型端点声明；配置后两个路径均不得含模型位置，且必须与实际目标模型完全一致。</summary>
    public string? FixedUpstreamModel { get; init; }
    /// <summary>Header/Query 必须明确的头或查询参数名；其他认证方式必须省略。</summary>
    public string? CredentialName { get; init; }
    /// <summary>Header/Query 中选中密钥前的固定非密钥文本。</summary>
    public string CredentialPrefix { get; init; } = "";
    /// <summary>Header/Query 中选中密钥后的固定非密钥文本。</summary>
    public string CredentialSuffix { get; init; } = "";
}
