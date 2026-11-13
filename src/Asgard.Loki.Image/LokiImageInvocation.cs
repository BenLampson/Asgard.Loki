namespace Asgard.Loki.Image;

/// <summary>单次调用的固定上游及借用传输工厂，不在单例 Handler 中保存请求状态。</summary>
public sealed class LokiImageInvocation
{
    /// <summary>创建完整调用上下文。</summary>
    public LokiImageInvocation(LokiImageUpstreamProvider provider, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(httpClientFactory);
        Provider = provider; HttpClientFactory = httpClientFactory;
    }
    /// <summary>当前调用的固定完整目标。</summary>
    public LokiImageUpstreamProvider Provider { get; }
    /// <summary>应用拥有的 HTTP 工厂，不能由 Handler 释放。</summary>
    public IHttpClientFactory HttpClientFactory { get; }
}
