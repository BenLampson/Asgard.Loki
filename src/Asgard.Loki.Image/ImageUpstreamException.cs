namespace Asgard.Loki.Image;

/// <summary>保留图像供应商的原始 HTTP 错误。</summary>
public sealed class ImageUpstreamException(string message, int statusCode, string responseBody) : Exception(message)
{
    /// <summary>上游 HTTP 状态码。</summary>
    public int StatusCode { get; } = statusCode;
    /// <summary>未删减的错误正文。</summary>
    public string ResponseBody { get; } = responseBody;
}
