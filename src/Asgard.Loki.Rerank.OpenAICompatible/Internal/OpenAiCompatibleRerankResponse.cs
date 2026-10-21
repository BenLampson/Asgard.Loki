using System.Text.Json;

namespace Asgard.Loki.Rerank.OpenAICompatible.Internal;

/// <summary>校验原生 results/index/relevance_score 对象，不要求供应商特有身份、用量或概率范围。</summary>
internal static class OpenAiCompatibleRerankResponse
{
    /// <summary>已知原生错误及没有排名的错误详情均保留，不变为成功空列表。</summary>
    internal static bool IsError(JsonElement root) => root.ValueKind == JsonValueKind.Object && (
        root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
        || root.TryGetProperty("object", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "error"
        || !root.TryGetProperty("results", out _) && (root.TryGetProperty("detail", out _) || root.TryGetProperty("message", out _)));
    /// <summary>只保留原数组次序；有请求上下文时验证完整数量、精确索引与真实候选回显。</summary>
    internal static void Validate(JsonElement root, JsonElement? documents = null, int? expected = null, bool requireDocuments = false)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0 || expected is { } count && results.GetArrayLength() != count || IsError(root))
            throw new InvalidDataException("兼容重排缺少完整原生 results 或数量不符合输入/top_n。");
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            if (usage.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生 usage 必须为对象或空值。");
            foreach (var name in new[] { "prompt_tokens", "completion_tokens", "total_tokens" })
                if (usage.TryGetProperty(name, out var number) && !OpenAiCompatibleRerankNumber.IsNonNegativeInteger(number)) throw new InvalidDataException("原生已知用量字段必须为精确非负整数。");
        }
        var seen = new HashSet<int>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity) || !OpenAiCompatibleRerankNumber.TryInt32(identity, out var index)
                || index < 0 || !seen.Add(index) || documents is { } originals && index >= originals.GetArrayLength()
                || !item.TryGetProperty("relevance_score", out var score) || score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) || !double.IsFinite(value))
                throw new InvalidDataException("重排结果缺少精确、唯一的输入索引或有限原生分数。");
            if (documents is not { } source) continue;
            var present = item.TryGetProperty("document", out var document);
            if (!present && requireDocuments) throw new InvalidDataException("上游遗漏显式要求的原生文档回显。");
            if (present && !Matches(source[index], document)) throw new InvalidDataException("文档回显与原始索引候选不关联，不能自行渲染或修复。");
        }
    }
    /// <summary>允许原始 JSON、原生 text 包装和保留原对象字段的回显；不比较虚构格式化文本。</summary>
    private static bool Matches(JsonElement original, JsonElement returned)
    {
        if (JsonElement.DeepEquals(original, returned)) return true;
        if (returned.ValueKind != JsonValueKind.Object) return false;
        if (returned.TryGetProperty("text", out var text) && JsonElement.DeepEquals(original, text)) return true;
        if (original.ValueKind != JsonValueKind.Object || !original.EnumerateObject().Any()) return false;
        return original.EnumerateObject().All(field => returned.TryGetProperty(field.Name, out var value) && JsonElement.DeepEquals(field.Value, value));
    }
}
