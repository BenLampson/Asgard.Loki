namespace Asgard.Loki.LLM.Coze;

/// <summary>可持久化的原生 Chat 引用，绑定固定目标和独立 Conversation/Chat ID，不含凭据或任意请求 URL。</summary>
public sealed record CozeChatReference
{
    /// <summary>完整根/Bot/凭据/实现/策略指纹，仅为绑定校验，不替代上游授权。</summary>
    public required string TargetIdentity { get; init; }
    /// <summary>原生会话 ID，不能使用 Message ID 替换。</summary>
    public required string ConversationId { get; init; }
    /// <summary>原生 Chat ID，与 Message ID、工具 ID 及 SSE ID 分开保留。</summary>
    public required string ChatId { get; init; }
}
