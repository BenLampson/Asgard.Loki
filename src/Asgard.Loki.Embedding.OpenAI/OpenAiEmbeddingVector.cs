using System.Text.Json;
using Asgard.Loki.Embedding.OpenAI.Internal;

namespace Asgard.Loki.Embedding.OpenAI;

/// <summary>保留单个原生 OpenAI 向量项、实际索引和编码，不自动解码或重排。</summary>
public sealed class OpenAiEmbeddingVector
{
    /// <summary>验证并保存独立原生快照。</summary>
    public OpenAiEmbeddingVector(JsonElement data)
    {
        _ = OpenAiEmbeddingResponse.Item(data); Data = data.Clone();
    }
    /// <summary>完整原生向量项，包括未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>原始输入关联索引，不等同于返回数组的位置。</summary>
    public int Index => Data.GetProperty("index").GetInt32();
    /// <summary>Float 编码时的数值视图；Base64 编码时为空，不把原始 JSON 改成另一种编码。</summary>
    public IReadOnlyList<double>? Values => Data.GetProperty("embedding").ValueKind == JsonValueKind.Array
        ? Data.GetProperty("embedding").EnumerateArray().Select(v => v.GetDouble()).ToArray() : null;
    /// <summary>Base64 原文；Float 编码时为空。</summary>
    public string? Base64 => Data.GetProperty("embedding").ValueKind == JsonValueKind.String ? Data.GetProperty("embedding").GetString() : null;
    /// <summary>按调用者显式要求解码 Base64 字节；不猜测本机或远端浮点端序。</summary>
    public ReadOnlyMemory<byte> DecodeBytes() => Base64 is { } text ? Convert.FromBase64String(text)
        : throw new InvalidOperationException("当前向量使用 Float 编码，没有 Base64 字节。");
}
