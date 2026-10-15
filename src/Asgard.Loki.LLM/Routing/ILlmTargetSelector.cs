namespace Asgard.Loki.LLM;

/// <summary>只为尚未绑定的会话选择完整上游目标。</summary>
public interface ILlmTargetSelector
{
    /// <summary>从已经过协议能力筛选的目标中选择一个。</summary>
    LlmTarget Select(IReadOnlyList<LlmTarget> candidates);
}
