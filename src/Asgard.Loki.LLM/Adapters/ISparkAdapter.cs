namespace Asgard.Loki.LLM;

/// <summary>实现星火原生生成；不共享其他协议的正文、认证、连接或生命周期。</summary>
public interface ISparkAdapter : ILlmProvider
{
    /// <summary>完整收集一次原生调用的消息，不提取首个文本或重算用量。</summary>
    Task<SparkCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>按原生顺序转交完整 WebSocket JSON 消息，不补 SSE、DONE 或错误终态。</summary>
    IAsyncEnumerable<SparkEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
