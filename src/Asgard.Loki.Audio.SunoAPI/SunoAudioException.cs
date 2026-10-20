using System.Net;
using System.Text.Json;

namespace Asgard.Loki.Audio.SunoAPI;

/// <summary>原生 HTTP 或业务错误，保留完整响应而不拼接可能敏感的上游文本。</summary>
public sealed class SunoAudioException : Exception
{
    /// <summary>复制错误正文与可用的原生对象，HTTP 失败时不强制解析正文。</summary>
    public SunoAudioException(HttpStatusCode statusCode, string? nativeCode, ReadOnlyMemory<byte> body, string? contentType, JsonElement? data = null)
        : base("Suno-API 音频任务调用返回原生错误。")
    { StatusCode = statusCode; NativeCode = nativeCode; Body = body.ToArray(); ContentType = contentType; Response = data?.Clone(); }
    /// <summary>实际 HTTP 状态。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>原生字符串 code；HTTP 错误正文未解析时为空。</summary>
    public string? NativeCode { get; }
    /// <summary>完整原始响应字节。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>原始响应内容类型。</summary>
    public string? ContentType { get; }
    /// <summary>已解析的完整原生业务错误对象。</summary>
    public JsonElement? Response { get; }
}
