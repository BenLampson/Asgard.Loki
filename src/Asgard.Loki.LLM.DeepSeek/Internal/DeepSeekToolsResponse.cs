using System.Text.Json;

namespace Asgard.Loki.LLM.DeepSeek.Internal;

/// <summary>原生完整结果形状验证，不把搜索评分、权威级别或空内容变成应用策略。</summary>
internal static class DeepSeekToolsResponse
{
    /// <summary>空 results 和空 title/content 仍是原生有效结果；不去重、排序、阈值过滤或补字段。</summary>
    internal static void Validate(JsonElement data, bool fetch)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("DeepSeek 原生工具结果必须是 JSON 对象。");
        if (fetch) { RequiredString(data, "title"); RequiredString(data, "content"); return; }
        if (!data.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) throw new InvalidDataException("DeepSeek search 结果缺少原生 results 数组。");
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生 search 条目必须为对象。");
            foreach (var name in new[] { "title", "url", "snippet", "summary", "content" }) RequiredString(item, name);
            if (!item.TryGetProperty("rerank_score", out var score) || score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidDataException("原生搜索分数必须是有限数值。");
            if (!item.TryGetProperty("authority", out var authority) || authority.ValueKind != JsonValueKind.Number || !authority.TryGetInt64(out _)) throw new InvalidDataException("原生 authority 必须为整数。");
            foreach (var name in new[] { "site_name", "site_icon", "date" })
                if (item.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("原生可空元数据必须为文本或 null。");
        }
    }
    /// <summary>不填充缺失的核心字段；空文本保持上游原文，不自动生成内容。</summary>
    private static void RequiredString(JsonElement data, string name)
    { if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException("原生结果缺少文本字段 " + name + "。"); }
}
