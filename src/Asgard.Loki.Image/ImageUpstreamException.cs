using System.Text;

namespace Asgard.Loki.Image;

/// <summary>保留图像生成的实际 HTTP 状态、原始错误正文及可选原生 SSE 身份。</summary>
public sealed class ImageUpstreamException : Exception
{
    private readonly string? _legacyText;
    /// <summary>保留原有文本构造入口；Body 是该文本的 UTF-8 投影，ContentType 不伪造。</summary>
    public ImageUpstreamException(string message, int statusCode, string responseBody) : base(message)
    {
        ArgumentNullException.ThrowIfNull(responseBody); StatusCode = statusCode; _legacyText = responseBody; Body = Encoding.UTF8.GetBytes(responseBody);
    }
    /// <summary>复制真实上游字节，不将未知编码或二进制错误体替换成有损文本。</summary>
    public ImageUpstreamException(string message, int statusCode, ReadOnlyMemory<byte> body, string? contentType) : base(message)
    { StatusCode = statusCode; Body = body.ToArray(); ContentType = contentType; }
    /// <summary>实际 HTTP 状态，含携带原生业务错误的成功状态。</summary>
    public int StatusCode { get; }
    /// <summary>旧文本构造时返回原文；字节构造时显式严格 UTF-8 解码，无效编码请读取 Body。</summary>
    public string ResponseBody => _legacyText ?? new UTF8Encoding(false, true).GetString(Body.Span);
    /// <summary>完整原始错误字节，或旧文本构造时的 UTF-8 投影。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>原始内容类型及 charset；未提供时为空。</summary>
    public string? ContentType { get; }
    /// <summary>可选原生生成 SSE 错误事件，含完整 JSON、目标和 SSE 身份。</summary>
    public ImageGenerationEvent? GenerationEvent { get; init; }
}
