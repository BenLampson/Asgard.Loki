using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Asgard.Loki.Rerank.Cohere.Internal;

/// <summary>精确验证原生整数和单位区间，不修改原始 JSON 数字表示。</summary>
internal static class CohereRerankNumber
{
    /// <summary>允许数值上精确的整数表示，拒绝浮点舍入后才成为整数的原始值。</summary>
    internal static bool TryInt32(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var candidate)
            || !double.IsFinite(candidate) || candidate < int.MinValue || candidate > int.MaxValue
            || Math.Truncate(candidate) != candidate) return false;
        number = (int)candidate;
        return JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number));
    }

    /// <summary>验证原始十进制数位确实位于零至一之间，避免负数下溢或一附近舍入掩盖越界。</summary>
    internal static bool TryScore(JsonElement value, out double score)
    {
        score = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out score) || !double.IsFinite(score)) return false;
        var raw = value.GetRawText(); var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var negative = mantissa[0] == '-'; if (negative) mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.'); var position = dot < 0 ? mantissa.Length : dot;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal); var first = 0;
        while (first < digits.Length && digits[first] == '0') first++;
        if (first == digits.Length) return true;
        if (negative) return false;
        var exponent = exponentAt < 0 ? BigInteger.Zero : BigInteger.Parse(raw.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var magnitude = exponent + position - first - 1;
        if (magnitude < 0) return true;
        if (magnitude > 0 || digits[first] != '1') return false;
        return digits.AsSpan(first + 1).IndexOfAnyExcept('0') < 0;
    }
}
