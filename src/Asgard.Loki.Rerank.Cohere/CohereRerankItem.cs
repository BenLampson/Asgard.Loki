using System.Text.Json;
using Asgard.Loki.Rerank.Cohere.Internal;

namespace Asgard.Loki.Rerank.Cohere;

/// <summary>供应商自有的原生排名项视图，保留所有字段和原始文档索引。</summary>
public sealed class CohereRerankItem
{
    /// <summary>验证索引和分数并保存完整独立快照，不推测原始请求文档或重算排名。</summary>
    public CohereRerankItem(JsonElement data) { _ = CohereRerankResponse.Item(data); Data = data.Clone(); }
    /// <summary>完整原生排名项，包括未知字段与原生数字表示。</summary>
    public JsonElement Data { get; }
    /// <summary>原始输入文档索引，可能不同于返回数组的位置。</summary>
    public int Index { get { _ = CohereRerankNumber.TryInt32(Data.GetProperty("index"), out var index); return index; } }
    /// <summary>原生分数的 double 便捷视图；精确 JSON 数字保留在 Data 中，不代表跨查询可比较的概率。</summary>
    public double RelevanceScore => Data.GetProperty("relevance_score").GetDouble();
    /// <summary>原生返回文档；缺失时为空，显式 JSON null 仍保留为 JsonElement。</summary>
    public JsonElement? Document => Data.TryGetProperty("document", out var document) ? document : null;
}
