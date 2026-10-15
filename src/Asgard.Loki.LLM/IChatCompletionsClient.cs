namespace Asgard.Loki.LLM;

/// <summary>对外提供统一的 Chat Completions 调用。</summary>
public interface IChatCompletionsClient
{
    /// <summary>执行一次完整生成。</summary>
    Task<ChatCompletion> CompleteAsync(ChatCompletionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
    /// <summary>逐个读取协议分块。</summary>
    IAsyncEnumerable<ChatCompletionEvent> StreamAsync(ChatCompletionRequest request, LlmCallOptions options, CancellationToken cancellationToken = default);
}
