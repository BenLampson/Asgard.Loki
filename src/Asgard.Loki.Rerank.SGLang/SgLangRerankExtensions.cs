using Asgard.Loki.Rerank.SGLang.Internal;

namespace Asgard.Loki.Rerank.SGLang;

/// <summary>供应商自己的数组视图，不改变核心结果或依赖其他供应商。</summary>
public static class SgLangRerankExtensions
{
    extension(RerankResult result)
    {
        /// <summary>校验原生条目并保留数组次序；无请求上下文时不猜测文档数量或身份。</summary>
        public IReadOnlyList<SgLangRerankItem> SgLangRankings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); SgLangRerankResponse.Validate(result.Data);
                return result.Data.EnumerateArray().Select(item => new SgLangRerankItem(item)).ToArray();
            }
        }
    }
}
