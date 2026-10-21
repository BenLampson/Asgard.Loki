using System.Text.Json;

namespace Asgard.Loki.Rerank.Cohere.Internal;

/// <summary>校验原生结果、唯一输入索引和已请求的文档关联，不重排或补分数。</summary>
internal static class CohereRerankResponse
{
    /// <summary>公共视图只验证结果本身；适配器另传本次请求以验证完整数量和文档身份。</summary>
    internal static void Validate(JsonElement root, CohereRerankPayload? payload = null, CohereRerankApiVersion? version = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidDataException("Cohere 重排响应缺少完整 results。");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("错误对象不能成为有效重排结果。");
        if (payload is not null && results.GetArrayLength() != payload.ExpectedCount)
            throw new InvalidDataException("重排结果数量与本次输入和 top_n 不一致。");
        var seen = new HashSet<int>();
        foreach (var item in results.EnumerateArray())
        {
            var index = Item(item);
            if (!seen.Add(index) || payload is not null && index >= payload.Documents.GetArrayLength())
                throw new InvalidDataException("重排索引重复或不属于本次输入。");
            if (payload is null || version != CohereRerankApiVersion.V1) continue;
            var present = item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null;
            if (!present)
            {
                if (payload.RequireDocuments) throw new InvalidDataException("上游遗漏显式要求的返回文档。");
                continue;
            }
            if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Cohere v1 返回文档缺少原生 text。");
            var source = payload.Documents[index]; var expected = source.ValueKind == JsonValueKind.String ? source : source.GetProperty("text");
            if (!string.Equals(text.GetString(), expected.GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("返回文档与原始输入索引不关联。");
            if (source.ValueKind == JsonValueKind.Object)
                foreach (var property in source.EnumerateObject())
                    if (!document.TryGetProperty(property.Name, out var returned) || !JsonElement.DeepEquals(property.Value, returned))
                        throw new InvalidDataException("返回文档遗漏或改写了原索引对应的文档字段。");
        }
    }

    /// <summary>解析原始非负整数索引及零至一的有限分数；不把数组次序当作输入索引。</summary>
    internal static int Item(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity)
            || !CohereRerankNumber.TryInt32(identity, out var index) || index < 0
            || !item.TryGetProperty("relevance_score", out var score) || !CohereRerankNumber.TryScore(score, out _))
            throw new InvalidDataException("重排项缺少有效原始索引或原生相关性分数。");
        return index;
    }
}
