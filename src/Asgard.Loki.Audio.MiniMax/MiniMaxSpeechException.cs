using System.Net;
using System.Text.Json;

namespace Asgard.Loki.Audio.MiniMax;

/// <summary>供应商原生 HTTP 或业务失败，保留完整原文及业务码。</summary>
public sealed class MiniMaxSpeechException : Exception
{
    private readonly byte[] _bytes;
    /// <summary>复制原始响应所有权；非 JSON HTTP 失败没有原生业务码。</summary>
    public MiniMaxSpeechException(HttpStatusCode statusCode, long? nativeCode, ReadOnlyMemory<byte> rawResponse,
        string? contentType = null, JsonElement? data = null) : base("MiniMax 原生语音调用失败。")
    { StatusCode = statusCode; NativeCode = nativeCode; _bytes = rawResponse.ToArray(); ContentType = contentType; NativeData = data?.Clone(); }
    /// <summary>原始 HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>原生 base_resp.status_code。</summary>
    public long? NativeCode { get; }
    /// <summary>原始正文副本。</summary>
    public ReadOnlyMemory<byte> RawResponse => _bytes.ToArray();
    /// <summary>原始 HTTP 内容类型。</summary>
    public string? ContentType { get; }
    /// <summary>完整原生 JSON，非 JSON HTTP 错误为空。</summary>
    public JsonElement? NativeData { get; }
}
