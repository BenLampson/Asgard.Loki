namespace Asgard.Loki.LLM;

/// <summary>独立原生 generate 调用入口，共享完整目标绑定而不共享 Chat 协议。</summary>
public interface IOllamaGenerateClient
{
    /// <summary>显式非流式 generate，不自动保存或回传 context。</summary>
    Task<OllamaGenerateResponse> GenerateAsync(OllamaGenerateRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>显式原生 generate NDJSON，不自动重连或续写。</summary>
    IAsyncEnumerable<OllamaGenerateEvent> StreamAsync(OllamaGenerateRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
