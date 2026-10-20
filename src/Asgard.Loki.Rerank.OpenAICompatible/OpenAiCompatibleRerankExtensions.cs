using Asgard.Loki.Rerank.OpenAICompatible.Internal;

namespace Asgard.Loki.Rerank.OpenAICompatible;

/// <summary>供应商包自己的原生兼容结果视图，不添加统一排序业务层。</summary>
public static class OpenAiCompatibleRerankExtensions
{
    extension(RerankResult result)
    {
        /// <summary>校验原生结果并保留数组次序；无请求上下文时不推测候选范围或内容。</summary>
        public IReadOnlyList<OpenAiCompatibleRerankItem> OpenAiCompatibleRankings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); OpenAiCompatibleRerankJson.Validate(result.Data); OpenAiCompatibleRerankResponse.Validate(result.Data);
                return result.Data.GetProperty("results").EnumerateArray().Select(item => new OpenAiCompatibleRerankItem(item)).ToArray();
            }
        }
    }
}
