using System.Net;

namespace Asgard.Loki.Audio.Volcengine;

/// <summary>保留 V1 原始失败报文和完整无符号业务码，不缩窄为 Int32。</summary>
public sealed class VolcengineV1SpeechException : Exception
{
    private readonly byte[] _raw;
    private readonly byte[] _payload;
    /// <summary>复制供应商原始错误所有权；负载可为非 JSON 二进制。</summary>
    public VolcengineV1SpeechException(long? nativeCode, ReadOnlyMemory<byte> rawResponse,
        ReadOnlyMemory<byte> decodedPayload, HttpStatusCode? statusCode = null, string? contentType = null)
        : base("火山 V1 语音调用失败。")
    {
        NativeCode = nativeCode; _raw = rawResponse.ToArray(); _payload = decodedPayload.ToArray();
        StatusCode = statusCode; ContentType = contentType;
    }
    /// <summary>原生业务码；HTTP 非 JSON 错误允许为空。</summary>
    public long? NativeCode { get; }
    /// <summary>原始完整 HTTP 响应或 WebSocket 二进制报文。</summary>
    public ReadOnlyMemory<byte> RawResponse => _raw.ToArray();
    /// <summary>原始或解压后的错误负载。</summary>
    public ReadOnlyMemory<byte> DecodedPayload => _payload.ToArray();
    /// <summary>HTTP 状态码；WebSocket 业务错误为空。</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>原始 HTTP 内容类型。</summary>
    public string? ContentType { get; }
}
