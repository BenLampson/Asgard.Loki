namespace Asgard.Loki.LLM;

/// <summary>调用原生文本补全协议，保留 prompt/token 输入，不隐式转成 Chat 消息。</summary>
public interface ITextCompletionsClient
{
    /// <summary>在固定完整 Session 目标执行一次原生完整 JSON 生成。</summary>
    Task<TextCompletion> CompleteAsync(TextCompletionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>逐条转交原生 SSE JSON 事件，供应商负责原生终态和资源完整性。</summary>
    IAsyncEnumerable<TextCompletionEvent> StreamAsync(TextCompletionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
