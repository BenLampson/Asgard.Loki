using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Asgard.Loki.Rerank.SiliconFlow.Internal;

/// <summary>按原生 JSON 的精确整数语义验证选项和计数，不经浮点舍入修复输入。</summary>
internal static class SiliconFlowRerankNumber
{
    /// <summary>只有实际数组索引或有界选项才需要精确的 Int32 表达。</summary>
    internal static bool TryInt32(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var candidate) || !double.IsFinite(candidate)
            || candidate < int.MinValue || candidate > int.MaxValue || Math.Truncate(candidate) != candidate) return false;
        number = (int)candidate; return JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number));
    }
    /// <summary>正整数选项和非负原生计数不被 Int32/Int64 上限截断，也不按指数分配巨大数组。</summary>
    internal static bool IsInteger(JsonElement value, bool positive = false)
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
