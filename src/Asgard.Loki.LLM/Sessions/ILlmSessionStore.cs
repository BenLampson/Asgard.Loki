namespace Asgard.Loki.LLM;

/// <summary>提供会话绑定和响应归属的共享存储契约。</summary>
public interface ILlmSessionStore
{
    /// <summary>读取会话；存储不可用必须传播错误。</summary>
    ValueTask<LlmSessionBinding?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>原子决定首次绑定，返回竞争后实际生效的绑定。</summary>
    ValueTask<LlmSessionBinding> GetOrCreateAsync(LlmSessionBinding candidate, CancellationToken cancellationToken = default);
    /// <summary>记录原始响应 ID 的归属并续期会话。</summary>
    ValueTask BindResponseAsync(LlmSessionBinding binding, string responseId, CancellationToken cancellationToken = default);
    /// <summary>验证响应属于当前绑定，并续期已验证的响应记录。</summary>
    ValueTask<bool> OwnsResponseAsync(LlmSessionBinding binding, string responseId, CancellationToken cancellationToken = default);
    /// <summary>续期已存在的会话绑定。</summary>
    ValueTask RefreshAsync(string sessionId, CancellationToken cancellationToken = default);
}
