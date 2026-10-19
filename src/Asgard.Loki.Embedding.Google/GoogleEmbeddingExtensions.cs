using Asgard.Loki.Embedding.Google.Internal;

namespace Asgard.Loki.Embedding.Google;

/// <summary>由 Google 供应商包提供原生视图，核心不承担不同供应商的结果统一。</summary>
public static class GoogleEmbeddingExtensions
{
    extension(EmbeddingResult result)
    {
        /// <summary>单条返回一项，批返回严格保持原数组次序；关联输入靠 Google 原生顺序契约而非虚构 index。</summary>
        public IReadOnlyList<GoogleEmbeddingVector> GoogleEmbeddings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result);
                return GoogleEmbeddingResponse.Read(result.Data).Select(item => new GoogleEmbeddingVector(item)).ToArray();
            }
        }
    }
}
