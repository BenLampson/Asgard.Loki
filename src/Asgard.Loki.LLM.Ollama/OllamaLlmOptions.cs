namespace Asgard.Loki.LLM.Ollama;

/// <summary>原生 HTTP/NDJSON 的本地资源上限，不声明模型或服务端配额。</summary>
public sealed record OllamaLlmOptions
{
    /// <summary>单条 NDJSON 记录的原始 UTF-8 字节上限，不含 LF；超限失败，不截断。</summary>
    public int MaximumStreamRecordBytes { get; init; } = 1024 * 1024;
    /// <summary>完整 JSON 和 HTTP 错误正文的字节上限；读取时限额，不先分配整份超限正文。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
}
