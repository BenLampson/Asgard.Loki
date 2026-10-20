namespace Asgard.Loki.LLM.Tests;

/// <summary>在实际目标选择时触发外部变更，验证请求已在首次可能等待之前复制。</summary>
internal sealed class ModerationTestSelector(Action? onSelect = null) : ILlmTargetSelector
{
    internal int Calls { get; private set; }
    public LlmTarget Select(IReadOnlyList<LlmTarget> candidates) { Calls++; onSelect?.Invoke(); return candidates[0]; }
}
