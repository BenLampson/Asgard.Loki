namespace Asgard.Loki.Embedding;

/// <summary>封装完整嵌入上游目标的原生适配契约，不依赖 LLM 或会话缓存。</summary>
public interface IEmbeddingAdapter
{
    /// <summary>供应商实例身份；实现持有自己的服务器、实际模型和凭据配置。</summary>
    string Id { get; }
    /// <summary>执行一次完整原生嵌入调用；输入格式和同步批输入由供应商协议定义。</summary>
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);
}
