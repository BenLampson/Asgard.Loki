using System.Text.Json;

namespace Asgard.Loki.Rerank.Ali.Internal;

/// <summary>验证原生 output.results、请求标识、用量和原始文档关联，不补造成功外壳。</summary>
internal static class AliRerankResponse
{
    /// <summary>原生 code/message 的业务错误即使伴随结果或 HTTP 200 也不能当作成功。</summary>
    internal static bool IsError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) return true;
        foreach (var name in new[] { "code", "message" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length != 0)) return true;
        return false;
    }

    /// <summary>原始数组次序不变，省略的模型字段不从配置伪造；本次调用另验证数量与对应内容。</summary>
    internal static void Validate(JsonElement root, AliRerankPayload? payload = null)
    {
        if (root.ValueKind != JsonValueKind.Object || IsError(root)
            || !root.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0
            || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("total_tokens", out var tokens) || !AliRerankNumber.IsNonNegativeInteger(tokens))
            throw new InvalidDataException("DashScope 响应缺少有效 request_id、output.results 或原生用量。");
        if (usage.TryGetProperty("prompt_tokens", out var prompt) && !AliRerankNumber.IsNonNegativeInteger(prompt))
            throw new InvalidDataException("原生 prompt_tokens 必须是非负整数。");
        if (payload is not null && results.GetArrayLength() != payload.ExpectedCount)
            throw new InvalidDataException("原生结果数量与本次文档及 top_n 不一致。");
        var seen = new HashSet<int>();
        foreach (var item in results.EnumerateArray())
        {
            var index = Item(item);
            if (!seen.Add(index) || payload is not null && index >= payload.Documents.GetArrayLength())
                throw new InvalidDataException("原生索引重复或不属于本次文档。");
            if (payload is null) continue;
            var present = item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null;
            if (!present && payload.RequireDocuments) throw new InvalidDataException("上游遗漏明确请求返回的文档。");
            if (!present) continue;
            _ = AliRerankDocument.TryContent(document, out var kind, out var content);
            _ = AliRerankDocument.TryContent(payload.Documents[index], out var sourceKind, out var source);
            // 原生返回的 text 包装可回显文档内容；显式媒体模态仍必须与输入匹配。
            if (kind != sourceKind && kind != "text" || !string.Equals(content, source, StringComparison.Ordinal))
                throw new InvalidDataException("返回文档的模态或内容与原始索引不关联。");
        }
    }

    /// <summary>只读取非负原始索引和原生单位区间分数，不重算或重排。</summary>
    internal static int Item(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity)
            || !AliRerankNumber.TryInt32(identity, out var index) || index < 0
            || !item.TryGetProperty("relevance_score", out var score) || !AliRerankNumber.TryScore(score, out _))
            throw new InvalidDataException("原生重排项缺少有效索引或零至一的有限分数。");
        if (item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null
            && (document.ValueKind != JsonValueKind.Object || !AliRerankDocument.TryContent(document, out _, out _)))
            throw new InvalidDataException("返回文档必须为有效原生单模态对象。");
        return index;
    }
}
