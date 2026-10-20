namespace Asgard.Loki.Audio.Azure;

/// <summary>Azure 原生 HTTP/业务失败，保留完整错误字节，不改写嵌套错误码。</summary>
public sealed class AzureAudioException : Exception
{
    private readonly byte[] _body;
    /// <summary>复制失败正文；成功 HTTP 状态也可能携带原生失败。</summary>
    public AzureAudioException(int statusCode, ReadOnlyMemory<byte> body, string? contentType) : base("Azure 原生音频调用失败。")
    { StatusCode = statusCode; _body = body.ToArray(); ContentType = contentType; }
    /// <summary>实际 HTTP 状态，不以业务码替代。</summary>
    public int StatusCode { get; }
    /// <summary>完整原始失败字节副本，包括非 UTF-8 内容。</summary>
    public ReadOnlyMemory<byte> Body => _body.ToArray();
    /// <summary>原始内容类型，允许未提供。</summary>
    public string? ContentType { get; }
}
