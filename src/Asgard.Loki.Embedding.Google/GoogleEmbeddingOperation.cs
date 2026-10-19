namespace Asgard.Loki.Embedding.Google;

/// <summary>固定 Google 目标的原生同步嵌入操作，不从输入数量猜测操作或创建异步批任务。</summary>
public enum GoogleEmbeddingOperation
{
    /// <summary>一次原生 Content 对应一个数值向量。</summary>
    EmbedContent,
    /// <summary>同步批请求，按 requests 原顺序返回 embeddings。</summary>
    BatchEmbedContents
}
