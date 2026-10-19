using System.Text.Json;
using Asgard.Loki.Rerank.SiliconFlow.Internal;

namespace Asgard.Loki.Rerank.SiliconFlow;

/// <summary>供应商自有排名视图，索引属于原始输入，不等于返回顺序。</summary>
public sealed class SiliconFlowRerankItem
{
    /// <summary>校验单项并保存完整独立原文；请求关联另由适配器检查。</summary>
    public SiliconFlowRerankItem(JsonElement data) { _ = SiliconFlowRerankResponse.Item(data); Data = data.Clone(); }
    /// <summary>完整原生项，保留数字精度、文档外壳和未来字段。</summary>
    public JsonElement Data { get; }
    /// <summary>原始输入文档的非负索引。</summary>
    public int Index { get { _ = SiliconFlowRerankNumber.TryInt32(Data.GetProperty("index"), out var number); return number; } }
    /// <summary>原生有限分数的 double 便捷视图；精确 JSON 数值仍在 Data 中。</summary>
    public double RelevanceScore => Data.GetProperty("relevance_score").GetDouble();
    /// <summary>可选原生文档对象；显式 null 与字段省略仍可区分。</summary>
    public JsonElement? Document => Data.TryGetProperty("document", out var document) ? document : null;
}
