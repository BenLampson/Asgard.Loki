namespace Asgard.Loki.LLM.Cloudflare;

/// <summary>原生文本 HTTP/SSE 的本地资源策略，不声明远端模型或服务配额。</summary>
public sealed record CloudflareTextOptions
{
    /// <summary>请求 UTF-8 字节上限，默认一 MiB；不分批、截断或重试。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整 JSON 和 HTTP 错误正文的原始字节上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>原始 SSE 全流字节上限，包含控制字段、注释和分隔符，默认六十四 MiB。</summary>
    public long MaximumStreamBytes { get; init; } = 64 * 1024 * 1024;
}
