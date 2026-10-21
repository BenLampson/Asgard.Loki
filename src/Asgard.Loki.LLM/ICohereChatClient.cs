namespace Asgard.Loki.LLM;

/// <summary>调用原生 Cohere v1 Chat 协议，保留 message/history/tool_results，不隐式转成 Chat 消息。</summary>
public interface ICohereChatClient
{
    /// <summary>在固定完整 Session 目标执行一次原生完整 JSON 生成。</summary>
    Task<CohereChatCompletion> CompleteAsync(CohereChatRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>逐条转交原生 NDJSON 事件，供应商负责原生终态和资源完整性。</summary>
    IAsyncEnumerable<CohereChatEvent> StreamAsync(CohereChatRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
