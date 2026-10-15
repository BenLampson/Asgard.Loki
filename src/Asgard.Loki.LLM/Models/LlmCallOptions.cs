namespace Asgard.Loki.LLM;

/// <summary>独立于上游协议内容的调用控制信息。</summary>
public sealed record LlmCallOptions
{
    /// <summary>由调用方保证归属隔离的稳定会话标识。</summary>
    public required string SessionId { get; init; }
}
