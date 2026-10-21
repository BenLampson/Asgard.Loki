using Asgard.Loki.Rerank.Cohere.Internal;

namespace Asgard.Loki.Rerank.Cohere;

/// <summary>在供应商包提供排名视图，不把 Cohere 协议结构搬入能力核心。</summary>
public static class CohereRerankExtensions
{
    extension(RerankResult result)
    {
        /// <summary>验证结果本身并保持原始排名数组次序；本次输入的数量及文档关联由适配器验证。</summary>
        public IReadOnlyList<CohereRerankItem> CohereRankings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); CohereRerankResponse.Validate(result.Data);
                return result.Data.GetProperty("results").EnumerateArray().Select(item => new CohereRerankItem(item)).ToArray();
            }
        }
    }
}
