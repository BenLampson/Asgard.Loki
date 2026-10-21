using System.Buffers.Text;
using System.Text.Json;

namespace Asgard.Loki.Embedding.Jina.Internal;

/// <summary>只校验当前支持的 float 数组与 float32 字节组 Base64，不做解码、归一化或格式替换。</summary>
internal static class JinaEmbeddingVectorCodec
{
    /// <summary>同一数值维度单位适用于单向量及每个 token 向量；稀疏和 packed 解释不猜测。</summary>
    internal static (int Size, JsonValueKind Kind) Read(JsonElement vector)
    {
        if (vector.ValueKind == JsonValueKind.Array)
        {
            if (vector.GetArrayLength() == 0) throw new InvalidDataException("原生向量不能为空。");
            foreach (var value in vector.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new InvalidDataException("float 向量必须只包含有限 JSON 数值。");
            return (vector.GetArrayLength(), vector.ValueKind);
        }
        if (vector.ValueKind == JsonValueKind.String && Base64.IsValid(vector.GetString(), out var bytes) && bytes > 0 && bytes % 4 == 0)
            return (bytes / 4, vector.ValueKind);
        throw new InvalidDataException("当前原生返回只支持非空数值向量或完整 float32 字节组 Base64，不展开 sparse/packed 输出。");
    }
}
