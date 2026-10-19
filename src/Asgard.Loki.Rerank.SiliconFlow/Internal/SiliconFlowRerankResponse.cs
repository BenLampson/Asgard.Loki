using System.Text.Json;

namespace Asgard.Loki.Rerank.SiliconFlow.Internal;

/// <summary>验证 SiliconFlow 原生身份、排名、可选计数及原文档关联，不重建 Jina/Cohere 响应。</summary>
internal static class SiliconFlowRerankResponse
{
    /// <summary>已知业务错误即使附带貌似有效的排名也不能变成成功；零代码不被补写到正常响应。</summary>
    internal static bool IsError(JsonElement root) => root.ValueKind == JsonValueKind.Object && (
        root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
        || root.TryGetProperty("code", out var code) && !JsonElement.DeepEquals(code, JsonSerializer.SerializeToElement(0))
        || !root.TryGetProperty("results", out _) && (root.TryGetProperty("message", out _) || root.TryGetProperty("detail", out _)));

    /// <summary>无请求上下文时只验证原生结果形状；适配器另验证完整数量和原始输入关联。</summary>
    internal static void Validate(JsonElement root, SiliconFlowRerankPayload? payload = null)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString()!.Length == 0
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0 || IsError(root))
            throw new InvalidDataException("SiliconFlow 响应缺少原生身份、有效排名或包含错误。");
        if (payload is not null && results.GetArrayLength() != payload.ExpectedCount) throw new InvalidDataException("返回数量不符合原始候选和 top_n。");
        if (root.TryGetProperty("tokens", out var legacy)) Counters(legacy);
        if (root.TryGetProperty("meta", out var meta))
        {
            if (meta.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SiliconFlow meta 必须为原生对象。");
            if (meta.TryGetProperty("tokens", out var tokens)) Counters(tokens);
        }
        HashSet<int> seen = [];
        foreach (var item in results.EnumerateArray())
        {
            var index = Item(item);
            if (!seen.Add(index) || payload is not null && index >= payload.Documents.GetArrayLength()) throw new InvalidDataException("排名索引重复或不属于原始输入。");
            if (payload is null) continue;
            var present = item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null;
            if (!present) { if (payload.RequireDocuments) throw new InvalidDataException("上游遗漏显式要求的文档。"); continue; }
            _ = SiliconFlowRerankDocument.TryContent(document, out var returnedKind, out var content);
            _ = SiliconFlowRerankDocument.TryContent(payload.Documents[index], out var originalKind, out var original);
            if (returnedKind != "text" && returnedKind != originalKind || !string.Equals(content, original, StringComparison.Ordinal))
                throw new InvalidDataException("返回文档内容或显式媒体种类与原始索引不关联。");
        }
    }
    /// <summary>分数只要求有限原生数值；不套用其他供应商的单位区间或把缺项当零。</summary>
    internal static int Item(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index) || !SiliconFlowRerankNumber.TryInt32(index, out var number) || number < 0
            || !item.TryGetProperty("relevance_score", out var score) || score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) || !double.IsFinite(value))
            throw new InvalidDataException("排名项必须包含有效的原始索引和有限分数。");
        if (item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null
            && (document.ValueKind != JsonValueKind.Object || !SiliconFlowRerankDocument.TryContent(document, out _, out _)))
            throw new InvalidDataException("返回文档必须为原生文本/图片内容对象。");
        return number;
    }
    /// <summary>仅校验已知原生 token 数字，缺失保持未知；不推算用量或解释计费元数据。</summary>
    private static void Counters(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SiliconFlow tokens 必须为原生对象。");
        foreach (var name in new[] { "input_tokens", "output_tokens", "image_tokens" })
            if (value.TryGetProperty(name, out var counter) && !SiliconFlowRerankNumber.IsInteger(counter)) throw new InvalidDataException("原生 token 计数必须为精确非负整数。");
    }
}
