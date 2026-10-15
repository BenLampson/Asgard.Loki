namespace Asgard.Loki.LLM;

/// <summary>将某个供应商的文本能力适配为 Chat Completions 结果。</summary>
public interface IChatCompletionsAdapter : ILlmProvider
{
    /// <summary>执行指定完整目标上的一次生成。</summary>
    Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>读取原始协议分块，不重新生成或改写工具内容。</summary>
    IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
