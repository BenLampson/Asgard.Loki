namespace Asgard.Loki.LLM;

/// <summary>Converse 专属原生入口，共享完整目标绑定而不共享其他协议的消息形状。</summary>
public interface IBedrockConverseClient
{
    /// <summary>显式完整 Converse 调用，不自动保存消息、媒体或提示变量。</summary>
    Task<BedrockConverseResponse> ConverseAsync(BedrockConverseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>显式 ConverseStream 调用，不重新连接或重放请求。</summary>
    IAsyncEnumerable<BedrockConverseEvent> StreamAsync(BedrockConverseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
