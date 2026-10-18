namespace Asgard.Loki.LLM;

/// <summary>提供 ClaudeMessages 原生调用接口。</summary>
public interface IClaudeMessagesClient
{
    /// <summary>获取完整原生响应。</summary>
    Task<ClaudeMessage> CreateAsync(ClaudeMessageRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>读取原生流式事件。</summary>
    IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(ClaudeMessageRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
