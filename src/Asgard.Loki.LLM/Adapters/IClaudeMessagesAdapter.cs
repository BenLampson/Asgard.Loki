namespace Asgard.Loki.LLM;

/// <summary>提供 ClaudeMessages 原生协议适配，不转换其他协议。</summary>
public interface IClaudeMessagesAdapter : ILlmProvider
{
    /// <summary>执行一次原生 JSON 调用。</summary>
    Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
    /// <summary>读取原生协议事件。</summary>
    IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default);
}
