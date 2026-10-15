namespace Asgard.Loki.LLM;

/// <summary>将已确定的目标、凭据与公共身份交给适配器。</summary>
public sealed class LlmAdapterContext(LlmTarget target, LlmCredential credential, string publicModel, string sessionId)
{
    /// <summary>本次不可由适配器另行调度的上游目标。</summary>
    public LlmTarget Target { get; } = target;
    /// <summary>本次认证凭据。</summary>
    public LlmCredential Credential { get; } = credential;
    /// <summary>稳定的逻辑会话。</summary>
    public string SessionId { get; } = sessionId;
    /// <summary>不包含密钥的结果元数据。</summary>
    public LlmResultMetadata Metadata { get; } = new(publicModel, target.UpstreamModel, target.ProviderId, target.Id);
}
