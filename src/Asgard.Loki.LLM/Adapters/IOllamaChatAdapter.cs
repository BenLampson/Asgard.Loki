namespace Asgard.Loki.LLM;

/// <summary>提供 Ollama 原生 api/chat 适配，不替换成 OpenAI Chat 或执行工具。</summary>
public interface IOllamaChatAdapter : ILlmProvider
{
    /// <summary>执行一次完整原生 JSON 调用。</summary>
    Task<OllamaChatResponse> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>读取完整原生 NDJSON 记录。</summary>
    IAsyncEnumerable<OllamaChatEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
