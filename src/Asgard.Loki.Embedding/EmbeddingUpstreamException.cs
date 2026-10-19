namespace Asgard.Loki.Embedding;

/// <summary>保留嵌入供应商的原始 HTTP 错误，由原生适配器抛出而不触发重试。</summary>
public sealed class EmbeddingUpstreamException(string message, int statusCode, string responseBody) : Exception(message)
{
    /// <summary>原始 HTTP 状态码。</summary>
    public int StatusCode { get; } = statusCode;
    /// <summary>完整原始错误正文，不折叠为成功结果或空向量列表。</summary>
    public string ResponseBody { get; } = responseBody;
}
