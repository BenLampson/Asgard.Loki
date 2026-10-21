using System.Net;

namespace Asgard.Loki.Video;

/// <summary>保留供应商 HTTP/API 失败的原始字节和状态，不混同任务自身的失败终态。</summary>
public sealed class VideoUpstreamException : Exception
{
    private readonly byte[] _body;
    /// <summary>保存独立错误窗口，错误消息不嵌入认证配置。</summary>
    public VideoUpstreamException(string message, HttpStatusCode statusCode, ReadOnlyMemory<byte> responseBody, string? contentType = null) : base(message)
    { StatusCode = statusCode; _body = responseBody.ToArray(); ContentType = contentType; }
    /// <summary>原始 HTTP 状态，包括上游在 200 正文返回的原生 API 错误。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>原始错误字节的独立只读副本；由调用者决定如何脱敏展示。</summary>
    public ReadOnlyMemory<byte> ResponseBody => _body.ToArray();
    /// <summary>原始 Content-Type，不能据此猜测生成成功。</summary>
    public string? ContentType { get; }
}
