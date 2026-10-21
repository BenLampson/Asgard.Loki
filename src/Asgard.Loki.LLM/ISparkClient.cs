namespace Asgard.Loki.LLM;

/// <summary>星火原生调用入口；完整模式保留整个消息窗口，传输仍为同一个原生 WebSocket 协议。</summary>
public interface ISparkClient
{
    /// <summary>返回有序原生消息窗口，等待供应商完成终态、关闭及资源验证，不合成为 OpenAI 响应。</summary>
    Task<SparkCompletion> CompleteAsync(SparkRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>逐条交付完整原生 JSON 消息，供应商负责流式终态、关闭、分片和字节边界。</summary>
    IAsyncEnumerable<SparkEvent> StreamAsync(SparkRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
