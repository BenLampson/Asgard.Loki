namespace Asgard.Loki.LLM;

/// <summary>可选原生 Responses 压缩能力；不得由基础 Responses 适配能力推断。</summary>
public interface IResponsesCompactionAdapter : ILlmProvider
{
    /// <summary>向已经绑定的上游发起一次压缩请求；不重试、不重建历史。</summary>
    Task<ResponseCompaction> CompactAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
