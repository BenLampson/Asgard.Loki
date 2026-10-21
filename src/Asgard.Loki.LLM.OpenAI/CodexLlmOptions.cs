namespace Asgard.Loki.LLM.OpenAI;

/// <summary>Codex 原生 Responses 调用策略；完整 API 根及实际模型由既有 LlmTarget 明确指定。</summary>
public sealed record CodexLlmOptions
{
    /// <summary>固定账户及原生头，完整参与适配身份。</summary>
    public required CodexAccountOptions Account { get; init; }
    /// <summary>单次原生请求 UTF-8 字节上限，默认一 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整 JSON、错误或整个 SSE 原始字节上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>首个可能终态起暂存事件数上限，默认一百二十八。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
}
