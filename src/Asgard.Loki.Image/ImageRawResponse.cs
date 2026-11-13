namespace Asgard.Loki.Image;

/// <summary>完整原始 HTTP 响应正文、状态和媒体类型，不重新序列化 JSON。</summary>
public sealed class ImageRawResponse
{
    private readonly byte[] _body;
    /// <summary>复制原始正文，保持结果独立于传输资源。</summary>
    public ImageRawResponse(int statusCode, ReadOnlyMemory<byte> body, string? contentType)
    { StatusCode = statusCode; _body = body.ToArray(); ContentType = contentType; }
    /// <summary>原始 HTTP 状态。</summary>
    public int StatusCode { get; }
    /// <summary>原始字节，每次读取返回独立副本。</summary>
    public ReadOnlyMemory<byte> Body => _body.ToArray();
    /// <summary>原始媒体类型。</summary>
    public string? ContentType { get; }
}
