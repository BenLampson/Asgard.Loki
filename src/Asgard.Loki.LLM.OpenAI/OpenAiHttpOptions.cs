namespace Asgard.Loki.LLM.OpenAI;

/// <summary>HTTP/JSON 与 SSE 的本地资源策略，不声明模型能力或执行重试。</summary>
public sealed record OpenAiHttpOptions
{
    /// <summary>单次 JSON、错误或整个 SSE 的原始响应字节上限；超限失败，不截断。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>第一个可能终态起暂存的事件数量上限，包含用量及未知尾帧。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
}
