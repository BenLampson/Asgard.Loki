namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>模拟首次 Get 看见空缓存，但协调后的创建必须采用另一个调用已写入的胜出绑定。</summary>
internal sealed class StaleReadSessionStore(ILlmSessionStore inner) : ILlmSessionStore
{
    private int _reads;
    /// <summary>记录竞争者最初提出的目标，证明测试确实走过首次绑定路径。</summary>
    internal string? ProposedTarget { get; private set; }
    /// <inheritdoc />
    public ValueTask<LlmSessionBinding?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        => Interlocked.Increment(ref _reads) == 1 ? ValueTask.FromResult<LlmSessionBinding?>(null) : inner.GetAsync(sessionId, cancellationToken);
    /// <inheritdoc />
    public ValueTask<LlmSessionBinding> GetOrCreateAsync(LlmSessionBinding candidate, CancellationToken cancellationToken = default)
    {
        ProposedTarget = candidate.TargetId;
        return inner.GetOrCreateAsync(candidate, cancellationToken);
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
