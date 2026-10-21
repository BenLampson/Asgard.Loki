using System.Text.Json;
using Asgard.Loki.Embedding.Google.Internal;

namespace Asgard.Loki.Embedding.Google;

/// <summary>保留一个 Google 原生数值向量，不向原始响应添加索引或编码字段。</summary>
public sealed class GoogleEmbeddingVector
{
    /// <summary>验证并保存独立原生快照。</summary>
    public GoogleEmbeddingVector(JsonElement data)
    { _ = GoogleEmbeddingResponse.Validate(data); Data = data.Clone(); }
    /// <summary>完整原生向量对象，保留未知统计与形状字段。</summary>
    public JsonElement Data { get; }
    /// <summary>按原次序读取有限数值，原始 JSON 和数值字符串不被改写。</summary>
    public IReadOnlyList<double> Values => Data.GetProperty("values").EnumerateArray().Select(GoogleEmbeddingResponse.Number).ToArray();
}
