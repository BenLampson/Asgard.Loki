namespace Asgard.Loki.LLM;

/// <summary>Nova Invoke 的独立原生客户端，共享完整目标绑定，不共享其他协议的消息结构。</summary>
public interface IBedrockNovaClient
{
    /// <summary>执行一次完整原生调用，不管理历史、媒体或服务器任务。</summary>
    Task<BedrockNovaResponse> InvokeAsync(BedrockNovaRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>读取一次原生流，不重新连接、重试或重放。</summary>
    IAsyncEnumerable<BedrockNovaEvent> StreamAsync(BedrockNovaRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
