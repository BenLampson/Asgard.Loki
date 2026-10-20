using System.Globalization;
using System.Text.Json;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>精确判断原生索引/计数，不经过浮点舍入或按巨大指数展开内存。</summary>
internal static class AzureImageNumber
{
    /// <summary>数学非负整数可有小数/指数形式，极小负数或近整数小数不能被舍入成合法计数。</summary>
    internal static bool Integer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText(); var negative = raw.StartsWith('-'); if (negative) raw = raw[1..];
        var exponentIndex = raw.IndexOfAny(['e', 'E']); var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var point = mantissa.IndexOf('.'); var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        if (digits.All(character => character == '0')) return true;
        if (negative) return false;
        var fractional = point < 0 ? 0 : mantissa.Length - point - 1;
        var trailing = digits.Length - digits.TrimEnd('0').Length;
        if (exponentIndex < 0) return trailing >= fractional;
        var exponent = raw[(exponentIndex + 1)..];
        if (!long.TryParse(exponent, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            return !exponent.StartsWith('-');
        return parsed >= (long)fractional - trailing;
    }
    /// <summary>先精确验证整数性，再读取可由小范围整数精确表示的值；不重写原始 JSON。</summary>
    internal static int Small(JsonElement value, int minimum, int maximum)
    {
        if (!Integer(value) || !value.TryGetDouble(out var number) || number < minimum || number > maximum)
            throw new ArgumentException("Azure 图像原生流计数不是允许范围的整数。");
        return (int)number;
    }
    /// <summary>multipart 使用原始整数文本，不将字段值默默转换后发送。</summary>
    internal static int Text(string value, int minimum, int maximum)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum
            ? number : throw new ArgumentException("Azure 图像原生表单流计数不是允许范围的整数文本。");
}
