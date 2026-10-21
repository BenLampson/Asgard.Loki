namespace Asgard.Loki.LLM;

/// <summary>实现原生 Cohere v1 Chat 完整 JSON/NDJSON，独立于 Chat Completions 及其 delta 结构。</summary>
public interface ICohereChatAdapter : ILlmProvider
{
    /// <summary>只在确定的完整目标上生成，保留原生历史、引用和工具调用及扩展数据。</summary>
    Task<CohereChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>保持原生事件顺序和索引，不聚合文本、补造终态或自动重试。</summary>
    IAsyncEnumerable<CohereChatEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
