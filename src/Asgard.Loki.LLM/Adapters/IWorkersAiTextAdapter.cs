namespace Asgard.Loki.LLM;

/// <summary>原生 ai/run 文本适配契约；模型在 URL 而非原生正文，不等同于兼容 Chat/Responses。</summary>
public interface IWorkersAiTextAdapter : ILlmProvider
{
    /// <summary>使用选定完整目标执行一次原生完整调用，供应商负责 HTTP、原生结果及资源校验。</summary>
    Task<WorkersAiTextCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>交付完整原生 SSE 帧，供应商负责真实 DONE、EOF、错误、字节及资源终态。</summary>
    IAsyncEnumerable<WorkersAiTextEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
