using Asgard.Loki.Rerank.SiliconFlow.Internal;

namespace Asgard.Loki.Rerank.SiliconFlow;

/// <summary>原生排名视图只留在供应商包，不把供应商结构加入重排核心。</summary>
public static class SiliconFlowRerankExtensions
{
    extension(RerankResult result)
    {
        /// <summary>保留原始排名次序；缺少原请求时不宣称已验证文档身份、范围或返回数量。</summary>
        public IReadOnlyList<SiliconFlowRerankItem> SiliconFlowRankings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); SiliconFlowRerankResponse.Validate(result.Data);
                return result.Data.GetProperty("results").EnumerateArray().Select(item => new SiliconFlowRerankItem(item)).ToArray();
            }
        }
    }
}
