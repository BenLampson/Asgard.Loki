namespace Asgard.Loki.LLM;

/// <summary>提供 Gemini 原生调用接口。</summary>
public interface IGeminiClient
{
    /// <summary>获取完整原生响应。</summary>
    Task<GeminiContent> GenerateAsync(GeminiContentRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>读取原生流式事件。</summary>
    IAsyncEnumerable<GeminiContentEvent> StreamAsync(GeminiContentRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
