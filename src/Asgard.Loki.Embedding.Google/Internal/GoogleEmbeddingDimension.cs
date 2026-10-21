using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Embedding.Google.Internal;

/// <summary>读取原生 int32 维度而不改写其数字、字符串或指数表示，避免浮点舍入误判。</summary>
internal static class GoogleEmbeddingDimension
{
    /// <summary>ProtoJSON null 保持 unset；旧外壳与新配置的明确维度不得冲突。</summary>
    internal static int? Read(JsonObject request)
    {
        var legacy = Integer(Field(request, "outputDimensionality", "output_dimensionality"));
        var config = Field(request, "embedContentConfig", "embed_content_config");
        if (config is null) return legacy;
        if (config is not JsonObject settings) throw new ArgumentException("embedContentConfig 必须为对象或 null。");
        var current = Integer(Field(settings, "outputDimensionality", "output_dimensionality"));
        if (legacy is not null && current is not null && legacy != current) throw new ArgumentException("新旧原生输出维度冲突，SDK 不猜测优先级。");
        return current ?? legacy;
    }

    /// <summary>两个别名同时出现属于不明确的同字段重复，不选择其中之一。</summary>
    private static JsonNode? Field(JsonObject source, string camel, string snake)
    {
        if (source.ContainsKey(camel) && source.ContainsKey(snake)) throw new ArgumentException("同一个原生字段不能同时使用两个别名。");
        return source.TryGetPropertyValue(camel, out var value) ? value : source[snake];
    }

    /// <summary>JSON 数值与 ProtoJSON 数值字符串均须精确等于正 int32；DeepEquals 按十进制无舍入比较。</summary>
    private static int? Integer(JsonNode? value)
    {
        if (value is null) return null;
        var number = JsonSerializer.SerializeToElement(value);
        if (number.ValueKind == JsonValueKind.String)
        {
            var text = number.GetString()!;
            if (text.Length == 0 || text.Any(char.IsWhiteSpace)) throw new ArgumentException("维度字符串必须是原生 int32。");
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var plain))
            { if (plain <= 0) throw new ArgumentException("输出维度必须为正整数。"); return plain; }
            try { using var parsed = JsonDocument.Parse(text.StartsWith('+') ? text[1..] : text); number = parsed.RootElement.Clone(); }
            catch (JsonException error) { throw new ArgumentException("维度字符串必须是原生 int32。", error); }
        }
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var candidate) || !double.IsFinite(candidate)
            || candidate < 1 || candidate > int.MaxValue || candidate != Math.Truncate(candidate)
            || !JsonElement.DeepEquals(number, JsonSerializer.SerializeToElement((int)candidate)))
            throw new ArgumentException("输出维度必须精确等于正 int32。");
        return (int)candidate;
    }
}
