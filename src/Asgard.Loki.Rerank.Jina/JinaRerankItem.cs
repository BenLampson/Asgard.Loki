using System.Text.Json;
using Asgard.Loki.Rerank.Jina.Internal;

namespace Asgard.Loki.Rerank.Jina;

/// <summary>供应商自有的原生排名视图，保留原始索引、文档、向量和未知字段。</summary>
public sealed class JinaRerankItem
{
    /// <summary>验证原生项并保存独立快照；原请求关联仍由适配器校验。</summary>
    public JinaRerankItem(JsonElement data) { _ = JinaRerankResponse.Item(data); Data = data.Clone(); }
    /// <summary>完整原生排名项，不重写数字表示或文档容器。</summary>
    public JsonElement Data { get; }
    /// <summary>原始输入文档索引，可能与排名数组位置不同。</summary>
    public int Index { get { _ = JinaRerankNumber.TryInt32(Data.GetProperty("index"), out var index); return index; } }
    /// <summary>有限原生分数的 double 便捷视图；原始精度仍保存在 Data 中。</summary>
    public double RelevanceScore => Data.GetProperty("relevance_score").GetDouble();
    /// <summary>原生字符串、TextDoc 或 ImageDoc；缺失为空，显式 JSON null 仍可区分。</summary>
    public JsonElement? Document => Data.TryGetProperty("document", out var document) ? document : null;
    /// <summary>上游可选文档向量的独立数值视图，不引入 Embedding 能力或生成缺失向量。</summary>
    public IReadOnlyList<double>? Embedding => Data.TryGetProperty("embedding", out var vector) && vector.ValueKind == JsonValueKind.Array
        ? vector.EnumerateArray().Select(value => value.GetDouble()).ToArray() : null;
}
