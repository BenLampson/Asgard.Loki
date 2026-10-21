using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>仅比较原生数值，不经过浮点舍入或改写 JSON；巨大指数不展开为巨大十进制字符串。</summary>
internal static class TranscriptionJsonNumber
{
    /// <summary>保留极小负数的符号，不因浮点下溢将它视为零。</summary>
    internal static int Sign(JsonElement value) => Normalize(value).Sign;
    /// <summary>精确比较时间端点，避免相邻大整数或微小数在 double 中合并。</summary>
    internal static int Compare(JsonElement left, JsonElement right) => Compare(Normalize(left), Normalize(right));
    /// <summary>字节必须是数学上的整数并位于 0..255，不能靠浮点近似接受小数。</summary>
    internal static bool IsByte(JsonElement value)
    {
        var number = Normalize(value);
        return number.Sign >= 0 && Compare(number, (1, "255", new BigInteger(3))) <= 0
            && (number.Sign == 0 || number.Order >= number.Digits.TrimEnd('0').Length);
    }
    /// <summary>按符号、十进制数量级和有效数字比较，填充只在比较时逻辑进行。</summary>
    private static int Compare((int Sign, string Digits, BigInteger Order) left, (int Sign, string Digits, BigInteger Order) right)
    {
        if (left.Sign != right.Sign) return left.Sign.CompareTo(right.Sign);
        if (left.Sign == 0) return 0;
        var order = left.Order.CompareTo(right.Order); if (order != 0) return order * left.Sign;
        for (var index = 0; index < Math.Max(left.Digits.Length, right.Digits.Length); index++)
        {
            var a = index < left.Digits.Length ? left.Digits[index] : '0'; var b = index < right.Digits.Length ? right.Digits[index] : '0';
            if (a != b) return a.CompareTo(b) * left.Sign;
        }
        return 0;
    }
    /// <summary>JsonElement 已校验数字语法；仅保留原始有效数字与指数，不分配指数对应的零。</summary>
    private static (int Sign, string Digits, BigInteger Order) Normalize(JsonElement value)
    {
        var raw = value.GetRawText(); var negative = raw.StartsWith('-'); if (negative) raw = raw[1..];
        var exponentIndex = raw.IndexOfAny(['e', 'E']); var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var point = mantissa.IndexOf('.'); var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return (0, "0", BigInteger.Zero);
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var fractional = point < 0 ? 0 : mantissa.Length - point - 1;
        return (negative ? -1 : 1, digits, exponent + digits.Length - fractional);
    }
}
