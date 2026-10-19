namespace Asgard.Loki.LLM;

/// <summary>原生 Ollama generate 独立适配契约，不隐式改写成 Chat。</summary>
public interface IOllamaGenerateAdapter : ILlmProvider
{
    /// <summary>对已绑定完整目标执行原生非流式 generate。</summary>
    Task<OllamaGenerateResponse> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>对已绑定完整目标读取原生 generate NDJSON。</summary>
    IAsyncEnumerable<OllamaGenerateEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
