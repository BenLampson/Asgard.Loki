namespace Asgard.Loki.LLM;

/// <summary>由单个供应商实现原生 Moderations 完整 JSON，不进行自动协议转换。</summary>
public interface IModerationsAdapter : ILlmProvider
{
    /// <summary>仅调用已确定的完整目标，保留原生请求/结果，不执行媒体下载或应用策略。</summary>
    Task<Moderation> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
