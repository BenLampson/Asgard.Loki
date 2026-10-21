namespace Asgard.Loki.LLM.Cohere;

/// <summary>固定原生 HTTP/NDJSON 的本地字节策略，不声明模型配额。</summary>
public sealed record CohereLlmOptions
{
    /// <summary>单次请求的 UTF-8 字节上限，默认一 MiB，不截断或分批。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整 JSON 和 HTTP 错误正文上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>单条原生记录上限，默认四 MiB，包含 CR、不包含 LF。</summary>
    public int MaximumStreamRecordBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>包含全部分隔符的原始流总上限，默认六十四 MiB。</summary>
    public long MaximumStreamBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>从 stream-end 起保留的终态与未来元数据记录上限，默认一百二十八。</summary>
    public int MaximumBufferedTailRecords { get; init; } = 128;
    /// <summary>可选原生 X-Client-Name，省略时拒绝工厂隐式注入项目名。</summary>
    public string? ClientName { get; init; }
}
