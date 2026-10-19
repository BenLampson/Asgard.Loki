using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Asgard.Loki.Rerank.Jina.Internal;

/// <summary>验证原生整数语义，不以 double 舍入或自造用量修复原始 JSON。</summary>
internal static class JinaRerankNumber
{
    /// <summary>输入数组索引需要精确落在本机数组可表示的整数范围。</summary>
    internal static bool TryInt32(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var candidate)
            || !double.IsFinite(candidate) || candidate < int.MinValue || candidate > int.MaxValue
            || Math.Truncate(candidate) != candidate) return false;
        number = (int)candidate;
        return JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number));
    }

    /// <summary>原生 top_n 和计数可超过 Int32；直接检查十进制数位与指数，不展开巨大数值。</summary>
    internal static bool IsNonNegativeInteger(JsonElement value, bool positive = false)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText(); var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt]; var negative = mantissa[0] == '-';
        if (negative) mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.'); var decimals = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal); var trailing = 0;
        while (trailing < digits.Length && digits[^(trailing + 1)] == '0') trailing++;
        if (trailing == digits.Length) return !positive;
        if (negative) return false;
        var exponent = exponentAt < 0 ? BigInteger.Zero : BigInteger.Parse(raw.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return exponent >= decimals - trailing;
    }
}
