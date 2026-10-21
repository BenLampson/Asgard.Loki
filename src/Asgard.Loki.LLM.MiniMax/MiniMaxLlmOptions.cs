namespace Asgard.Loki.LLM.MiniMax;

/// <summary>原生 v2 单次调用的本地资源上限，不代表模型能力或上游配额。</summary>
public sealed record MiniMaxLlmOptions
{
    /// <summary>冻结后的完整 UTF-8 请求字节上限。</summary>
    public int MaximumRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整 JSON 和 HTTP 错误的字节上限。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>含注释、控制记录和结束分隔符的整个 SSE 字节上限。</summary>
    public int MaximumStreamBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>从首个 choice 结束开始暂存的事件数量上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>单次调用关联的不同 choice 数量上限，不按稀疏索引分配数组。</summary>
    public int MaximumTrackedChoices { get; init; } = 128;
}
