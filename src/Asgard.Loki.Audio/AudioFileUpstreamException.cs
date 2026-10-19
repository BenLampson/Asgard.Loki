namespace Asgard.Loki.Audio;

/// <summary>完整保留原生文件操作的 HTTP/业务失败正文，不将二进制或未知编码错误体转成有损文本。</summary>
public sealed class AudioFileUpstreamException : Exception
{
    /// <summary>复制原始失败字节及内容类型；状态为实际 HTTP 状态，不虚构业务状态。</summary>
    public AudioFileUpstreamException(string message, int code, ReadOnlyMemory<byte> body, string? contentType) : base(message)
    { Code = code; Body = body.ToArray(); ContentType = contentType; }
    /// <summary>原始 HTTP 状态码，包括成功 HTTP 状态携带的原生错误。</summary>
    public int Code { get; }
    /// <summary>完整原始错误正文，不替换无效 UTF-8 字节。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>原始内容类型的 .NET 表示，包括 charset；未提供时为空。</summary>
    public string? ContentType { get; }
    /// <summary>可选原生转录错误事件，保留完整 JSON、目标与 SSE 身份；普通 HTTP/文件错误不伪造事件。</summary>
    public AudioTranscriptionEvent? TranscriptionEvent { get; init; }
}
