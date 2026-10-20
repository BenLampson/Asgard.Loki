namespace Asgard.Loki.LLM.Tencent;

/// <summary>原生混元传输策略；实际服务地址和模型由完整 LLM 目标显式提供。</summary>
public sealed record TencentHunyuanOptions
{
    /// <summary>可选显式原生区域头；省略时不注入区域。</summary>
    public string? Region { get; init; }
    /// <summary>可选显式逻辑签名 Host，适用于物理代理；省略时使用实际目标 authority。</summary>
    public string? SigningHost { get; init; }
    /// <summary>完整原生请求 UTF-8 字节上限，在取得凭据前检查。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整响应原始字节上限，包含原生及 HTTP 错误。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
}
