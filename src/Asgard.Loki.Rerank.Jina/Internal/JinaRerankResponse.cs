using System.Text.Json;

namespace Asgard.Loki.Rerank.Jina.Internal;

/// <summary>校验 Jina 原生排名、文档关联和可选向量，不套用其他供应商的分数范围或文档回显规则。</summary>
internal static class JinaRerankResponse
{
    /// <summary>原生必需模型、用量和完整排名不得缺失；请求上下文另验证数量、索引与显式返回选项。</summary>
    internal static void Validate(JsonElement root, JinaRerankPayload? payload = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("total_tokens", out var tokens) || !JinaRerankNumber.IsNonNegativeInteger(tokens)
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidDataException("Jina 原生响应缺少有效模型、用量或完整 results。");
        if (root.TryGetProperty("object", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "list"))
            throw new InvalidDataException("Jina 重排响应 object 必须为 list。");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("错误对象不能成为有效重排结果。");
        if (payload is not null && results.GetArrayLength() != payload.ExpectedCount)
            throw new InvalidDataException("重排结果数量与本次输入和 top_n 不一致。");
        var seen = new HashSet<int>();
        foreach (var item in results.EnumerateArray())
        {
            var index = Item(item);
            if (!seen.Add(index) || payload is not null && index >= payload.Documents.GetArrayLength())
                throw new InvalidDataException("Jina 重排索引重复或不属于本次输入。");
            if (payload is null) continue;
            var hasDocument = item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null;
            if (!hasDocument && payload.RequireDocuments) throw new InvalidDataException("上游遗漏原生返回约定要求的文档。");
            if (hasDocument)
            {
                _ = JinaRerankDocument.TryContent(document, out var returnedKind, out var returned);
                _ = JinaRerankDocument.TryContent(payload.Documents[index], out var sourceKind, out var source);
                if (document.ValueKind != JsonValueKind.String && returnedKind != sourceKind
                    || !string.Equals(returned, source, StringComparison.Ordinal))
                    throw new InvalidDataException("Jina 返回文档的原生内容与输入索引不关联。");
            }
            if (payload.RequireEmbeddings && (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind == JsonValueKind.Null))
                throw new InvalidDataException("上游遗漏显式请求的文档向量。");
        }
    }

    /// <summary>分数仅要求有限数字，不猜测零至一范围；可选文档和向量按 Jina 原生类型读取。</summary>
    internal static int Item(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity)
            || !JinaRerankNumber.TryInt32(identity, out var index) || index < 0
            || !item.TryGetProperty("relevance_score", out var score) || score.ValueKind != JsonValueKind.Number
            || !score.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException("Jina 重排项缺少有效原始索引或有限分数。");
        if (item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null
            && !JinaRerankDocument.TryContent(document, out _, out _)) throw new InvalidDataException("Jina 返回文档类型无效。");
        if (item.TryGetProperty("embedding", out var embedding) && embedding.ValueKind != JsonValueKind.Null)
        {
            if (embedding.ValueKind != JsonValueKind.Array || embedding.GetArrayLength() == 0)
                throw new InvalidDataException("Jina 原生文档向量必须是非空数值数组。");
            foreach (var value in embedding.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var element) || !double.IsFinite(element))
                    throw new InvalidDataException("Jina 文档向量必须包含有限数值。");
        }
        return index;
    }
}
