using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>核对 JSON Schema 边界能否往返为上游 double，不通过舍入改变约束。</summary>
internal static class GeminiSchemaNumber
{
    private static readonly Regex Number = new(@"\A(?<negative>-?)(?<whole>[0-9]+)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>保留原始 JSON 数字，仅接受有限且最短 double 报文不改变十进制值的边界。</summary>
    internal static JsonNode Write(JsonNode? value, string path)
    {
        if (value is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number)
            throw new ArgumentException($"{path} 必须是 JSON 数字。");
        var element = JsonSerializer.SerializeToElement(value);
        if (!element.TryGetDouble(out var number) || !double.IsFinite(number)
            || Normalize(element.GetRawText()) is not { } original || original != Normalize(JsonSerializer.Serialize(number)))
            throw new LlmConversionException([new("schema_number_not_representable", path, "边界不能无十进制舍入地表示为目标 double。")]);
        return value.DeepClone();
    }

    /// <summary>按有效数字与十进制幂比较，接受等价指数写法而不分配巨大整数。</summary>
    private static string? Normalize(string raw)
    {
        var match = Number.Match(raw);
        if (!match.Success) return null;
        var fraction = match.Groups["fraction"].Value;
        var digits = (match.Groups["whole"].Value + fraction).TrimStart('0');
        if (digits.Length == 0) return "0";
        var significant = digits.TrimEnd('0'); var exponent = 0L;
        if (match.Groups["exponent"].Success
            && !long.TryParse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return null;
        if (exponent is > int.MaxValue or < -int.MaxValue) return null;
        var power = exponent - fraction.Length + digits.Length - significant.Length;
        return match.Groups["negative"].Value + significant + "e" + power.ToString(CultureInfo.InvariantCulture);
    }
}
