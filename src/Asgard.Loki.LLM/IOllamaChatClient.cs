namespace Asgard.Loki.LLM;

/// <summary>通过完整 Session 目标调用 Ollama 原生 Chat。</summary>
public interface IOllamaChatClient
{
    /// <summary>读取完整原生 JSON 结果。</summary>
    Task<OllamaChatResponse> CompleteAsync(OllamaChatRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>逐条读取原生流，不生成工具结果、重试或切换目标。</summary>
    IAsyncEnumerable<OllamaChatEvent> StreamAsync(OllamaChatRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
