using System.Buffers.Text;
using System.Text.Json;

namespace Asgard.Loki.Embedding.OpenAI.Internal;

/// <summary>校验完整原生批结果与实际输入索引；不重排、填补向量或转换编码。</summary>
internal static class OpenAiEmbeddingResponse
{
    /// <summary>原始顺序保留，但索引必须恰好覆盖本次输入，所有向量长度与格式保持一致。</summary>
    internal static void Validate(JsonElement root, int? expectedCount = null, int? dimensions = null, string? encoding = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidDataException("OpenAI 嵌入响应缺少有效 data。");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("错误对象不能成为有效嵌入结果。");
        if (root.TryGetProperty("object", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "list"))
            throw new InvalidDataException("OpenAI 嵌入响应类型无效。");
        var count = data.GetArrayLength();
        if (expectedCount is { } expected && count != expected) throw new InvalidDataException("嵌入结果数量与输入不一致。");
        var seen = new bool[count]; int? length = dimensions; JsonValueKind? format = null;
        foreach (var item in data.EnumerateArray())
        {
            var (index, size, valueKind) = Item(item);
            if (index >= count || seen[index]) throw new InvalidDataException("嵌入索引重复、缺失或不属于本次输入。");
            seen[index] = true;
            if (length is { } width && width != size) throw new InvalidDataException("嵌入维度不一致或不符合显式请求。");
            if (format is { } previous && previous != valueKind) throw new InvalidDataException("同一响应不能混用向量编码。");
            if (encoding is not null && (encoding == "float") != (valueKind == JsonValueKind.Array))
                throw new InvalidDataException("返回编码不符合显式请求。");
            length = size; format = valueKind;
        }
    }

    /// <summary>读取必需索引和完整向量；Base64 只校验语法与 float32 字节宽度，不解释端序或数值。</summary>
    internal static (int Index, int Size, JsonValueKind Kind) Item(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity)
            || identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt32(out var index) || index < 0
            || !item.TryGetProperty("embedding", out var embedding)) throw new InvalidDataException("嵌入项缺少合法索引或向量。");
        if (item.TryGetProperty("object", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "embedding"))
            throw new InvalidDataException("嵌入项类型无效。");
        if (embedding.ValueKind == JsonValueKind.Array)
        {
            var size = embedding.GetArrayLength(); if (size == 0) throw new InvalidDataException("嵌入向量不能为空。");
            foreach (var value in embedding.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new InvalidDataException("嵌入向量必须包含有限数值。");
            return (index, size, embedding.ValueKind);
        }
        if (embedding.ValueKind != JsonValueKind.String || !Base64.IsValid(embedding.GetString(), out var bytes) || bytes == 0 || bytes % 4 != 0)
            throw new InvalidDataException("嵌入 Base64 必须包含完整 float32 字节。");
        return (index, bytes / 4, embedding.ValueKind);
    }
}
