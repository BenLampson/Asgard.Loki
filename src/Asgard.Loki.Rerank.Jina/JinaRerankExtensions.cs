using Asgard.Loki.Rerank.Jina.Internal;

namespace Asgard.Loki.Rerank.Jina;

/// <summary>在 Jina 供应商包提供原生结果视图，核心不引入供应商结构或向量协调层。</summary>
public static class JinaRerankExtensions
{
    extension(RerankResult result)
    {
        /// <summary>校验原生结果并保留排名数组次序；没有原请求时不猜测本次文档范围或身份。</summary>
        public IReadOnlyList<JinaRerankItem> JinaRankings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); JinaRerankResponse.Validate(result.Data);
                return result.Data.GetProperty("results").EnumerateArray().Select(item => new JinaRerankItem(item)).ToArray();
            }
        }
    }
}
