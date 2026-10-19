namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Express 原生 Gemini 传输的本地资源策略，不包含标准模式项目、主体或令牌设置。</summary>
public sealed record GoogleVertexExpressLlmOptions
{
    /// <summary>完整 JSON、HTTP 错误或整次 SSE 的原始总字节上限。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>首个候选终态/提示拦截起的原生事件暂存上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>原始候选索引关联数上限，不按索引分配数组。</summary>
    public int MaximumTrackedCandidates { get; init; } = 128;
}
