namespace Asgard.Loki.LLM.Tests;

/// <summary>让并发测试中的不同客户端选择不同候选目标。</summary>
internal sealed class TestTargetSelector(int index) : ILlmTargetSelector
{
    /// <inheritdoc />
    public LlmTarget Select(IReadOnlyList<LlmTarget> candidates) => candidates[index % candidates.Count];
}
