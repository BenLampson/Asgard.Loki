using Asgard.Loki.Embedding.Jina.Internal;

namespace Asgard.Loki.Embedding.Jina;

/// <summary>在供应商包提供原生有索引项视图，不扩展核心通用模型或增加能力耦合。</summary>
public static class JinaEmbeddingExtensions
{
    extension(EmbeddingResult result)
    {
        /// <summary>校验原生外壳并保留返回项次序；本次输入数量和明确设置另由适配器校验。</summary>
        public IReadOnlyList<JinaEmbeddingItem> JinaEmbeddings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result);
                return JinaEmbeddingResponse.Read(result.Data).Select(v => new JinaEmbeddingItem(v)).ToArray();
            }
        }
    }
}
