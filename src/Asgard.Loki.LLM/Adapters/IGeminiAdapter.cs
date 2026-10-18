namespace Asgard.Loki.LLM;

/// <summary>提供 Gemini 原生协议适配，不转换其他协议。</summary>
public interface IGeminiAdapter : ILlmProvider
{
    /// <summary>执行一次原生 JSON 调用。</summary>
    Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>读取原生协议事件。</summary>
    IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
