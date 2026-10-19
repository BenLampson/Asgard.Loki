using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>精确读取 ProtoJSON int64，不通过浮点数舍入约束或展开巨大指数。</summary>
internal static class GeminiSchemaInteger
{
    private static readonly Regex Number = new(@"\A(?<sign>[+-]?)(?<whole>[0-9]+)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>接受带引号或数字形式的整数指数；负数、小数及溢出均明确失败。</summary>
    internal static long Read(JsonNode value, string path)
    {
        var raw = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        var match = Number.Match(raw);
        if (!match.Success) throw Invalid(path);
        var fraction = match.Groups["fraction"].Value;
        var digits = (match.Groups["whole"].Value + fraction).TrimStart('0');
        if (digits.Length == 0) return 0;
        if (match.Groups["sign"].Value == "-") throw Invalid(path);
        var significant = digits.TrimEnd('0');
        var exponent = 0L;
        if (match.Groups["exponent"].Success
            && !long.TryParse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            throw Invalid(path);
        // 输入长度最多为 Int32；超出这一范围的非零指数无法被小数位抵消。
        if (exponent is > int.MaxValue or < -int.MaxValue) throw Invalid(path);
        var power = exponent - fraction.Length + digits.Length - significant.Length;
        if (power < 0 || significant.Length + power > 19) throw Invalid(path);
        var integer = significant + new string('0', (int)power);
        if (!long.TryParse(integer, NumberStyles.None, CultureInfo.InvariantCulture, out var result)) throw Invalid(path);
        return result;
    }

    /// <summary>错误只包含字段路径，不回显可能敏感的 Schema 内容。</summary>
    private static ArgumentException Invalid(string path) => new($"{path} 必须是可精确表示的非负 int64 整数。");
}
