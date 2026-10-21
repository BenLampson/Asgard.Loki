using Asgard.Loki.Embedding.Ollama.Internal;

namespace Asgard.Loki.Embedding.Ollama;

/// <summary>供应商私有方言的便利视图不进入能力核心或统一抽象。</summary>
public static class OllamaEmbeddingExtensions
{
    extension(EmbeddingResult result)
    {
        /// <summary>验证完整原生外壳，按输入对应的原始数组顺序读取；零输入返回空列表。</summary>
        public IReadOnlyList<OllamaEmbeddingVector> OllamaEmbeddings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result);
                return OllamaEmbeddingResponse.Read(result.Data).Select(v => new OllamaEmbeddingVector(v)).ToArray();
            }
        }
    }
}
