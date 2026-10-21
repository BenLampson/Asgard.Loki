namespace Asgard.Loki.LLM;

/// <summary>在既有 LLM 路由和完整 Session 绑定下执行原生内容分类模型调用。</summary>
public interface IModerationsClient
{
    /// <summary>生成一次完整原生分类结果，不自动重试或改变输入。</summary>
    Task<Moderation> CreateAsync(ModerationRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
