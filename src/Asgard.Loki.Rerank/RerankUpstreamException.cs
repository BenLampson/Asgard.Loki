using System.Text;

namespace Asgard.Loki.Rerank;

/// <summary>保留重排供应商实际 HTTP 状态和完整错误正文，不触发重试或目标切换。</summary>
public sealed class RerankUpstreamException : Exception
{
    private readonly string? _legacyText;
    /// <summary>保留原有文本构造入口和原始文本，Body 为其 UTF-8 投影，ContentType 不伪造。</summary>
    public RerankUpstreamException(string message, int statusCode, string responseBody) : base(message)
    {
        ArgumentNullException.ThrowIfNull(responseBody); StatusCode = statusCode; _legacyText = responseBody; Body = Encoding.UTF8.GetBytes(responseBody);
    }
    /// <summary>复制实际原生错误字节，不通过文本解码替换二进制或未知编码正文。</summary>
    public RerankUpstreamException(string message, int statusCode, ReadOnlyMemory<byte> body, string? contentType) : base(message)
    { StatusCode = statusCode; Body = body.ToArray(); ContentType = contentType; }
    /// <summary>实际 HTTP 状态，包括含原生 API 错误的成功 HTTP 状态。</summary>
    public int StatusCode { get; }
    /// <summary>旧入口返回原文；字节入口显式严格 UTF-8 解码，无效编码请读取 Body。</summary>
    public string ResponseBody => _legacyText ?? new UTF8Encoding(false, true).GetString(Body.Span);
    /// <summary>完整原生字节，或旧文本构造入口的 UTF-8 投影。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>实际原始媒体类型及 charset，未提供时为空。</summary>
    public string? ContentType { get; }
}
