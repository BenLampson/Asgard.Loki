namespace Asgard.Loki.Audio;

/// <summary>保留音频供应商的 HTTP 或业务错误。</summary>
public sealed class SpeechUpstreamException(string message, int code, string responseBody) : Exception(message)
{
    /// <summary>原始 HTTP 状态码或供应商业务码。</summary>
    public int Code { get; } = code;
    /// <summary>未删减的供应商错误内容。</summary>
    public string ResponseBody { get; } = responseBody;
}
