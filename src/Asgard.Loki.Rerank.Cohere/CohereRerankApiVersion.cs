namespace Asgard.Loki.Rerank.Cohere;

/// <summary>显式选择 Cohere 原生重排版本，不根据模型名、输入类型或响应猜测。</summary>
public enum CohereRerankApiVersion
{
    /// <summary>原生 v1/rerank，支持文本或文档对象及原生返回文档选项。</summary>
    V1 = 1,
    /// <summary>原生 v2/rerank，接受字符串文档及其原生配置。</summary>
    V2 = 2
}
