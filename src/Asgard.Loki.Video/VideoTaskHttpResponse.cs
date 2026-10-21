using System.Net;

namespace Asgard.Loki.Video;

/// <summary>可选的原始视频 HTTP 响应窗口，只保存状态、媒体类型和独立正文，不承载供应商协议。</summary>
public sealed class VideoTaskHttpResponse
{
    private readonly byte[] _body;
    /// <summary>复制已由供应商完成读取、限制与释放检查的原始响应。</summary>
    public VideoTaskHttpResponse(HttpStatusCode statusCode, ReadOnlyMemory<byte> body, string? contentType = null)
    {
        if ((int)statusCode is < 100 or > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        StatusCode = statusCode; _body = body.ToArray(); ContentType = contentType;
    }
    /// <summary>原始 HTTP 状态，不据此推断供应商任务完成。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>原始正文的独立只读副本，空正文保持零字节。</summary>
    public ReadOnlyMemory<byte> Body => _body.ToArray();
    /// <summary>原始媒体类型，可以缺失。</summary>
    public string? ContentType { get; }
    /// <summary>仅用于确认原始正文确实为空，不复制整个窗口。</summary>
    internal bool IsEmpty => _body.Length == 0;
}
