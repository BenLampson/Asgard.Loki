using Asgard.Loki.Embedding.OpenAI.Internal;

namespace Asgard.Loki.Embedding.OpenAI;

/// <summary>供应商包提供原生向量视图，不在核心引入 OpenAI 响应结构。</summary>
public static class OpenAiEmbeddingExtensions
{
    extension(EmbeddingResult result)
    {
        /// <summary>验证并按原始数组次序读取向量；关联输入时使用每项 Index，不按数组位置猜测。</summary>
        public IReadOnlyList<OpenAiEmbeddingVector> OpenAiVectors
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); OpenAiEmbeddingResponse.Validate(result.Data);
                return result.Data.GetProperty("data").EnumerateArray().Select(item => new OpenAiEmbeddingVector(item)).ToArray();
            }
        }
    }
}
