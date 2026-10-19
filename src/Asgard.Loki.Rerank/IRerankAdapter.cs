namespace Asgard.Loki.Rerank;

/// <summary>封装完整重排上游目标的原生适配契约，不依赖 LLM、Embedding 或会话缓存。</summary>
public interface IRerankAdapter
{
    /// <summary>稳定的供应商实例身份；实现持有自己的服务器、实际模型、凭据及原生方言配置。</summary>
    string Id { get; }
    /// <summary>执行一次原生重排调用，供应商负责查询/文档格式及原文档索引和分数校验。</summary>
    Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default);
}
