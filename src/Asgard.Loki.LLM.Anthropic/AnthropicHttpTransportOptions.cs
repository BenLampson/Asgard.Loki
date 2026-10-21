namespace Asgard.Loki.LLM.Anthropic;

/// <summary>原生 Messages 操作相对路径与凭据格式；认证方式仍由 AnthropicHttpOptions 唯一指定。</summary>
public sealed record AnthropicHttpTransportOptions
{
    /// <summary>追加到完整目标根的明确相对操作路径，可携带固定查询；不接受其他服务器或模板。</summary>
    public string MessagesPath { get; init; } = "messages";
    /// <summary>Header/Query 必须明确提供的头或参数名；其他方式必须省略。</summary>
    public string? CredentialName { get; init; }
    /// <summary>Header/Query 中选中密钥前的固定非密钥文本。</summary>
    public string CredentialPrefix { get; init; } = "";
    /// <summary>Header/Query 中选中密钥后的固定非密钥文本。</summary>
    public string CredentialSuffix { get; init; } = "";
}
