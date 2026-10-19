using System.Text.Json;

namespace Asgard.Loki.Rerank;

/// <summary>保存完整原生重排响应，不重算分数、重排条目、补关联索引或虚构用量。</summary>
public sealed class RerankResult
{
    /// <summary>保存独立 JSON 快照；供应商负责校验自身的原生成功、错误与排序结果结构。</summary>
    public RerankResult(JsonElement data, RerankResultMetadata metadata)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("重排响应必须为原生 JSON 对象。", nameof(data));
        ArgumentNullException.ThrowIfNull(metadata);
        Data = data.Clone(); Metadata = metadata;
    }
    /// <summary>完整原生 JSON，保留排名、原文档索引、分数、用量及扩展字段。</summary>
    public JsonElement Data { get; }
    /// <summary>实际调用的固定目标身份。</summary>
    public RerankResultMetadata Metadata { get; }
}
