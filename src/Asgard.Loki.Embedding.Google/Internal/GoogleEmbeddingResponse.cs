using System.Globalization;
using System.Text.Json;

namespace Asgard.Loki.Embedding.Google.Internal;

/// <summary>校验 Developer API 数值向量和批顺序，不生成 OpenAI 索引或统一批内维度。</summary>
internal static class GoogleEmbeddingResponse
{
    /// <summary>一次原生返回只能有一个明确外壳，批结果按原顺序逐项匹配请求维度。</summary>
    internal static JsonElement[] Read(JsonElement root, GoogleEmbeddingOperation? operation = null, int?[]? dimensions = null)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Google 嵌入响应必须为对象。");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new InvalidDataException("错误响应不能成为数值嵌入。");
        var single = root.TryGetProperty("embedding", out var embedding); var batch = root.TryGetProperty("embeddings", out var embeddings);
        if (single == batch || operation == GoogleEmbeddingOperation.EmbedContent && !single || operation == GoogleEmbeddingOperation.BatchEmbedContents && !batch)
            throw new InvalidDataException("嵌入响应外壳与原生操作不一致。");
        JsonElement[] result;
        if (single) result = [embedding];
        else
        {
            if (embeddings.ValueKind != JsonValueKind.Array || embeddings.GetArrayLength() == 0) throw new InvalidDataException("批嵌入缺少向量数组。");
            result = embeddings.EnumerateArray().ToArray();
        }
        if (dimensions is not null && result.Length != dimensions.Length) throw new InvalidDataException("批返回数量与原始请求不一致。");
        for (var i = 0; i < result.Length; i++)
        {
            var size = Validate(result[i]);
            if (dimensions?[i] is { } expected && expected != size) throw new InvalidDataException("返回维度与对应原生请求不一致。");
        }
        return result;
    }

    /// <summary>只支持公开 Developer API 的非空数值向量；shape 等未知字段原样保留但不解读为向量。</summary>
    internal static int Validate(JsonElement embedding)
    {
        if (embedding.ValueKind != JsonValueKind.Object || !embedding.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0) throw new InvalidDataException("响应没有公开数值嵌入向量。");
        foreach (var value in values.EnumerateArray()) _ = Number(value);
        return values.GetArrayLength();
    }

    /// <summary>兼容 ProtoJSON 引号数值，保留原 JSON；NaN/Infinity 不能伪装成有效嵌入数值。</summary>
    internal static double Number(JsonElement value)
    {
        double number = 0;
        var valid = value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out number)
            : value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && !text.Any(char.IsWhiteSpace)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        if (!valid || !double.IsFinite(number)) throw new InvalidDataException("嵌入值必须是有限数值。");
        return number;
    }
}
