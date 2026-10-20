using System.Net;
using System.Text;

namespace Asgard.Loki.LLM;

/// <summary>保留上游实际 HTTP 状态、原生正文及可选媒体类型，不自动记录或重试。</summary>
public sealed class LlmUpstreamException : Exception
{
    private readonly string? _legacyText;
    /// <summary>保留原文本构造入口及 ResponseBody，Body 为该原文的 UTF-8 投影。</summary>
    public LlmUpstreamException(string message, HttpStatusCode statusCode, string responseBody) : base(message)
    {
        ArgumentNullException.ThrowIfNull(responseBody); StatusCode = statusCode; _legacyText = responseBody; Body = Encoding.UTF8.GetBytes(responseBody);
    }
    /// <summary>复制原始错误字节和媒体类型，不通过有损文本解码替换二进制正文。</summary>
    public LlmUpstreamException(string message, HttpStatusCode statusCode, ReadOnlyMemory<byte> body, string? contentType) : base(message)
    { StatusCode = statusCode; Body = body.ToArray(); ContentType = contentType; }
    /// <summary>实际 HTTP 状态，包括原生业务错误使用的成功 HTTP 状态。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>旧入口返回原文；字节入口显式严格 UTF-8 解码，无效编码请读取 Body。</summary>
    public string ResponseBody => _legacyText ?? new UTF8Encoding(false, true).GetString(Body.Span);
    /// <summary>完整原生错误字节，或旧文本入口的 UTF-8 投影，不自动写入日志。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>实际媒体类型及 charset；旧文本入口或上游未提供时为空。</summary>
    public string? ContentType { get; }
}
