namespace Asgard.Loki.LLM;

/// <summary>为新会话轮询选择目标，不影响已存在的 Session。</summary>
public sealed class RoundRobinLlmTargetSelector : ILlmTargetSelector
{
    private int _sequence = -1;

    /// <inheritdoc />
    public LlmTarget Select(IReadOnlyList<LlmTarget> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Count);
        return candidates[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)candidates.Count)];
    }
}
