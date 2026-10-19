using System.Text.Json;

namespace Asgard.Loki.Embedding.Jina.Internal;

/// <summary>原生 item 字段显式区分单/多向量，验证各输入自己的 token 列表。</summary>
internal static class JinaEmbeddingItemCodec
{
    /// <summary>允许多向量空 token 序列，但不允许混合外壳、错误 token 类型或错误向量。</summary>
    internal static (int Index, bool Multi, int? Width, JsonValueKind? Encoding) Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity)
            || !JinaEmbeddingNumber.TryInt32(identity, out var index) || index < 0)
            throw new InvalidDataException("原生 item 缺少非负输入索引。");
        var single = item.TryGetProperty("embedding", out var embedding); var multi = item.TryGetProperty("embeddings", out var embeddings);
        if (single == multi) throw new InvalidDataException("原生 item 必须且只能具有一种单/多向量外壳。");
        if (item.TryGetProperty("object", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != (multi ? "embeddings" : "embedding")))
            throw new InvalidDataException("原生 item object 与内容外壳不一致。");
        JsonElement[] vectors;
        if (multi)
        {
            if (embeddings.ValueKind != JsonValueKind.Array) throw new InvalidDataException("多向量 embeddings 必须为数组。");
            vectors = embeddings.EnumerateArray().ToArray();
            if (item.TryGetProperty("tokenized_input", out var tokens) && tokens.ValueKind != JsonValueKind.Null
                && (tokens.ValueKind != JsonValueKind.Array || tokens.GetArrayLength() != vectors.Length
                    || tokens.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String)))
                throw new InvalidDataException("tokenized_input 必须与本项向量数量对应，且只包含字符串。");
        }
        else vectors = [embedding];
        int? width = null; JsonValueKind? encoding = null;
        foreach (var vector in vectors)
        {
            var format = JinaEmbeddingVectorCodec.Read(vector);
            if (width is not null && width != format.Size || encoding is not null && encoding != format.Kind)
                throw new InvalidDataException("单项 token 向量必须具有一致维度和编码。");
            width = format.Size; encoding = format.Kind;
        }
        return (index, multi, width, encoding);
    }
}
