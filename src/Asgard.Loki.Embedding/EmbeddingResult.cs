using System.Text.Json;

namespace Asgard.Loki.Embedding;

/// <summary>保存完整供应商原生嵌入响应，不重排向量、补索引、转换编码或虚构用量。</summary>
public sealed class EmbeddingResult
{
    /// <summary>保存独立 JSON 快照；供应商负责校验自身的原生成功、错误与向量结构。</summary>
    public EmbeddingResult(JsonElement data, EmbeddingResultMetadata metadata)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("嵌入响应必须为原生 JSON 对象。", nameof(data));
        ArgumentNullException.ThrowIfNull(metadata);
        Data = data.Clone(); Metadata = metadata;
    }
    /// <summary>完整原生 JSON，保留索引、编码、向量、用量及扩展字段。</summary>
    public JsonElement Data { get; }
    /// <summary>实际调用的固定目标身份。</summary>
    public EmbeddingResultMetadata Metadata { get; }
}
