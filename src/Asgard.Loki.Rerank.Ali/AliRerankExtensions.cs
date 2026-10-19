using Asgard.Loki.Rerank.Ali.Internal;

namespace Asgard.Loki.Rerank.Ali;

/// <summary>供应商包提供自身的嵌套排名视图，不向能力核心引入 DashScope 方言。</summary>
public static class AliRerankExtensions
{
    extension(RerankResult result)
    {
        /// <summary>验证原生外壳并按原数组次序读取，输入对应关系由适配器结合请求验证。</summary>
        public IReadOnlyList<AliRerankItem> AliRankings
        {
            get
            {
                ArgumentNullException.ThrowIfNull(result); AliRerankResponse.Validate(result.Data);
                return result.Data.GetProperty("output").GetProperty("results").EnumerateArray().Select(v => new AliRerankItem(v)).ToArray();
            }
        }
    }
}
