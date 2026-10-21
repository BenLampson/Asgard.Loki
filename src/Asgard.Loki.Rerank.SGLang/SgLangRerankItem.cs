using System.Text.Json;

namespace Asgard.Loki.Rerank.SGLang;

/// <summary>一个原生排名条目，保持 score/index/document/meta_info，不假设概率范围。</summary>
public sealed class SgLangRerankItem
{
    internal SgLangRerankItem(JsonElement data) => Data = data.Clone();
    /// <summary>独立条目快照，包含完整原生字段和精确 JSON 数值。</summary>
    public JsonElement Data { get; }
    /// <summary>原始输入文档索引，和数组排名位置不同。</summary>
    public int Index => (int)Data.GetProperty("index").GetDouble();
    /// <summary>有限原生 score 的 double 便捷视图，原始精度仍在 Data 中。</summary>
    public double Score => Data.GetProperty("score").GetDouble();
    /// <summary>原生文档回显；缺失为空，显式 JSON null 保持可区分。</summary>
    public JsonElement? Document => Data.TryGetProperty("document", out var value) ? value : null;
    /// <summary>可选原生元信息，不补造用量或从逐项计数推算整次请求。</summary>
    public JsonElement? MetaInfo => Data.TryGetProperty("meta_info", out var value) ? value : null;
}
