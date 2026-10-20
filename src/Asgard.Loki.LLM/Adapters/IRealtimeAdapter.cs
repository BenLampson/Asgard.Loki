namespace Asgard.Loki.LLM;

/// <summary>供应商原生 Realtime 适配契约，保持独立协议及连接状态。</summary>
public interface IRealtimeAdapter : ILlmProvider
{
    /// <summary>从已绑定目标建立一次原生连接；返回连接拥有传输资源。</summary>
    Task<IRealtimeConnection> OpenRealtimeAsync(LlmAdapterContext context, CancellationToken cancellationToken = default);
}
