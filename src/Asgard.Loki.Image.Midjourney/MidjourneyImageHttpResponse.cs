namespace Asgard.Loki.Image.Midjourney;

/// <summary>一次原生代理交换的实际 HTTP 元数据与完整正文快照。</summary>
public sealed class MidjourneyImageHttpResponse
{
    internal MidjourneyImageHttpResponse(int statusCode, byte[] body, string? contentType)
    { StatusCode = statusCode; Body = body.ToArray(); ContentType = contentType; }
    /// <summary>实际 HTTP 状态，不由原生业务码替换。</summary>
    public int StatusCode { get; }
    /// <summary>完整原始正文字节，含批量结果中的全部任务。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>原始媒体类型和字符集，未提供时保持 null。</summary>
    public string? ContentType { get; }
}
