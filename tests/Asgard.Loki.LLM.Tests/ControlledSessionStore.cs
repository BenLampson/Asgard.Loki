namespace Asgard.Loki.LLM.Tests;

/// <summary>借用真实内存缓存存储，仅在会话读取边界模拟过期、替换或错误。</summary>
internal sealed class ControlledSessionStore(ILlmSessionStore inner) : ILlmSessionStore
{
    private int _getOrCreateCalls;
    /// <summary>可控的读取结果，不通过真实时钟制造过期竞态。</summary>
    internal Func<LlmSessionBinding?, LlmSessionBinding?>? ReadBinding { get; set; }
    /// <summary>首次绑定协调次数。</summary>
    internal int GetOrCreateCalls => Volatile.Read(ref _getOrCreateCalls);
    /// <inheritdoc />
    public async ValueTask<LlmSessionBinding?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await inner.GetAsync(sessionId, cancellationToken);
        return ReadBinding is null ? result : ReadBinding(result);
    }
    /// <inheritdoc />
    public ValueTask<LlmSessionBinding> GetOrCreateAsync(LlmSessionBinding candidate, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getOrCreateCalls); return inner.GetOrCreateAsync(candidate, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask BindResponseAsync(LlmSessionBinding binding, string responseId, CancellationToken cancellationToken = default)
        => inner.BindResponseAsync(binding, responseId, cancellationToken);
    /// <inheritdoc />
    public ValueTask<bool> OwnsResponseAsync(LlmSessionBinding binding, string responseId, CancellationToken cancellationToken = default)
        => inner.OwnsResponseAsync(binding, responseId, cancellationToken);
    /// <inheritdoc />
    public ValueTask RefreshAsync(string sessionId, CancellationToken cancellationToken = default)
        => inner.RefreshAsync(sessionId, cancellationToken);
}
