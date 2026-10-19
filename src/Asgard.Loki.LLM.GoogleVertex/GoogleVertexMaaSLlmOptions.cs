namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>MaaS Chat JSON/SSE 的本地传输策略，不承担云端项目或模型管理。</summary>
public sealed record GoogleVertexMaaSLlmOptions
{
    /// <summary>完整 JSON、HTTP 错误或整次 SSE 的原始总字节上限。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>显式 quota project 请求头；为空时禁止继承工厂默认项目身份。</summary>
    public string? QuotaProjectId { get; init; }
    /// <summary>从第一个 choice 原生终态开始的尾部暂存事件数上限。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>原生 choice 关联数上限；稀疏索引不用于数组分配。</summary>
    public int MaximumTrackedChoices { get; init; } = 128;
    /// <summary>整次调用的 choice/tool 索引关联数上限，不聚合工具参数。</summary>
    public int MaximumTrackedToolCalls { get; init; } = 128;
}
