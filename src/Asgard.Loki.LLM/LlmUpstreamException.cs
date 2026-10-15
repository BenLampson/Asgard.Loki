using System.Net;

namespace Asgard.Loki.LLM;

/// <summary>统一暴露上游 HTTP 错误并保留原始错误内容。</summary>
public sealed class LlmUpstreamException(string message, HttpStatusCode statusCode, string responseBody)
    : Exception(message)
{
    /// <summary>原始 HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
    /// <summary>未删减的上游错误正文；不会自动写入日志。</summary>
    public string ResponseBody { get; } = responseBody;
}
