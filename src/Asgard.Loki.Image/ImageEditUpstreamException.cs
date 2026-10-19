namespace Asgard.Loki.Image;

/// <summary>保留原生图像编辑的 HTTP/业务失败字节，不对未知编码或二进制错误正文作有损解码。</summary>
public sealed class ImageEditUpstreamException : Exception
{
    /// <summary>复制原始正文，状态码始终来自实际 HTTP 响应。</summary>
    public ImageEditUpstreamException(string message, int statusCode, ReadOnlyMemory<byte> body, string? contentType) : base(message)
    { StatusCode = statusCode; Body = body.ToArray(); ContentType = contentType; }
    /// <summary>原始 HTTP 状态，可能是携带业务错误的 2xx。</summary>
    public int StatusCode { get; }
    /// <summary>完整原始错误字节。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>原始内容类型及 charset；上游未提供时为空。</summary>
    public string? ContentType { get; }
}
