namespace Asgard.Loki.LLM;

/// <summary>可选原生 Responses WebSocket 适配扩展；基础 Responses HTTP 能力不能推导此能力。</summary>
public interface IResponsesWebSocketAdapter : ILlmProvider
{
    /// <summary>在已经确定的完整目标上建立连接；工厂由适配实现持有且不会被能力模块释放。</summary>
    Task<IResponsesWebSocketConnection> OpenWebSocketAsync(LlmAdapterContext context, CancellationToken cancellationToken = default);
}
