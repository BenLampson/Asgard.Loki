namespace Asgard.Loki.LLM.Azure;

/// <summary>Azure 原生端点与本地资源策略，不声明模型能力或自动选择版本。</summary>
public sealed record AzureLlmOptions
{
    /// <summary>明确使用 v1 或带版本的旧式 API 地址。</summary>
    public AzureLlmApiStyle ApiStyle { get; init; }
    /// <summary>Dated 必填；v1 仅在明确提供时追加，不注入默认 preview。</summary>
    public string? ApiVersion { get; init; }
    /// <summary>单次响应/HTTP 错误的原始字节上限；超限失败，不截断。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>首次可能终态起暂存的原生事件数上限，包含过滤与用量尾帧。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>单次 Chat 流允许关联的原始 choice 索引数，不重编号。</summary>
    public int MaximumTrackedChoices { get; init; } = 128;
}
