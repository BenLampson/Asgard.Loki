using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>验证原生结果与输入边界，保留原始类别、分数和未知模型字段，不执行内容处置策略。</summary>
internal static class OpenAiModerationResponse
{
    private static readonly string[] Known = ["harassment", "harassment/threatening", "hate", "hate/threatening", "illicit", "illicit/violent",
        "self-harm", "self-harm/instructions", "self-harm/intent", "sexual", "sexual/minors", "violence", "violence/graphic"];
    /// <summary>每个文本输入一个结果，多模态内容组一个结果；不补标签、重排条目或推算判断。</summary>
    internal static void Validate(JsonElement root, int expected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())
            || !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(model.GetString())
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != expected)
            throw new InvalidDataException("Moderations 缺少原生身份、模型或与输入边界一致的完整结果。");
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("flagged", out var flagged) || flagged.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !item.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("category_scores", out var scores) || scores.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Moderations 条目缺少原生分类字段。");
            var hasTypes = item.TryGetProperty("category_applied_input_types", out var types) && types.ValueKind != JsonValueKind.Null;
            if (hasTypes && types.ValueKind != JsonValueKind.Object) throw new InvalidDataException("类别输入类型必须为原生对象或空值。");
            foreach (var name in Known)
            {
                if (categories.TryGetProperty(name, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    && !(name is "illicit" or "illicit/violent" && flag.ValueKind == JsonValueKind.Null)) throw new InvalidDataException("已知类别标记必须为原生布尔或该类别允许的空值。");
                if (scores.TryGetProperty(name, out var score) && (score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) || !double.IsFinite(value)))
                    throw new InvalidDataException("已知类别分数必须为有限原生数字。");
                if (hasTypes && types.TryGetProperty(name, out var applied) && (applied.ValueKind != JsonValueKind.Array || applied.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
                    throw new InvalidDataException("已知类别输入类型必须为原生字符串数组。");
            }
        }
    }
}
