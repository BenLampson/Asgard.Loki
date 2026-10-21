using System.Text.Json;

namespace Asgard.Loki.Rerank.OpenAICompatible;

/// <summary>一个完整原生兼容排名条目，原始输入索引与数组排名保持独立。</summary>
public sealed class OpenAiCompatibleRerankItem
{
    internal OpenAiCompatibleRerankItem(JsonElement data) => Data = data.Clone();
    /// <summary>完整条目及原始 JSON 分数精度、未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>原始候选数组的精确索引。</summary>
    public int Index => (int)Data.GetProperty("index").GetDouble();
    /// <summary>有限原生分数的 double 视图，不强制单位区间、不重算或归一化。</summary>
    public double RelevanceScore => Data.GetProperty("relevance_score").GetDouble();
    /// <summary>可选原生候选回显，缺失为空，显式 JSON null 可区分。</summary>
    public JsonElement? Document => Data.TryGetProperty("document", out var value) ? value : null;
}
