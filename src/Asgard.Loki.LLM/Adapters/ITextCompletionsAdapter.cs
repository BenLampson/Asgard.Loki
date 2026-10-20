namespace Asgard.Loki.LLM;

/// <summary>实现原生文本补全完整 JSON/SSE，独立于 Chat Completions 及其 delta 结构。</summary>
public interface ITextCompletionsAdapter : ILlmProvider
{
    /// <summary>只在确定的完整目标上生成，保留原生文本选择、对数概率和扩展数据。</summary>
    Task<TextCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>保持原生事件顺序和索引，不聚合文本、补造终态或自动重试。</summary>
    IAsyncEnumerable<TextCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
