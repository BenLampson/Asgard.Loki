using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>精确验证原生索引/计数的非负整数性，不转浮点、不按指数展开内存或重写原文。</summary>
internal static class ImageEditJsonNumber
{
    /// <summary>数学整数可以带小数/指数形式；极小负数或近整数小数不会因舍入而被接受。</summary>
    internal static bool IsNonNegativeInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText(); var negative = raw.StartsWith('-'); if (negative) raw = raw[1..];
        var exponentIndex = raw.IndexOfAny(['e', 'E']); var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var point = mantissa.IndexOf('.'); var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        if (digits.All(c => c == '0')) return true;
        if (negative) return false;
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var fractional = point < 0 ? 0 : mantissa.Length - point - 1;
        var trailing = digits.Length - digits.TrimEnd('0').Length;
        return exponent - fractional >= -trailing;
    }
}
