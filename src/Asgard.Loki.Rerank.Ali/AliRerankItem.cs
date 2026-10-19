using System.Text.Json;
using Asgard.Loki.Rerank.Ali.Internal;

namespace Asgard.Loki.Rerank.Ali;

/// <summary>保留完整 DashScope 排名项，不将输出位置当作原文档索引。</summary>
public sealed class AliRerankItem
{
    /// <summary>验证已知字段后保存独立 JSON 快照。</summary>
    public AliRerankItem(JsonElement data) { _ = AliRerankResponse.Item(data); Data = data.Clone(); }
    /// <summary>完整原生排名项，保留原数值、文档与未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>对应输入 documents 的原始索引。</summary>
    public int Index { get { _ = AliRerankNumber.TryInt32(Data.GetProperty("index"), out var index); return index; } }
    /// <summary>原生分数便捷视图；精确 JSON 保留在 Data，不用于跨请求比较或校准。</summary>
    public double RelevanceScore => Data.GetProperty("relevance_score").GetDouble();
    /// <summary>原生返回文档；缺失为空，显式 JSON null 保持独立。</summary>
    public JsonElement? Document => Data.TryGetProperty("document", out var value) ? value : null;
}
