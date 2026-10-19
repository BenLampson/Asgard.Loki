using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Asgard.Loki.LLM.Anthropic.Internal;

/// <summary>精确读取 ProtoJSON int32，不通过浮点数舍入关联，也不按巨大指数展开内存。</summary>
internal static class AnthropicInteger
{
    private static readonly Regex Number = new(@"\A(?<sign>[+-]?)(?<whole>[0-9]+)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    /// <summary>候选索引可使用字符串或数字；枚举数字允许有符号的原始 int32。</summary>
    internal static int Read(JsonElement value, bool signed = false)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw Invalid();
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(); var match = Number.Match(raw); if (!match.Success) throw Invalid();
        var fraction = match.Groups["fraction"].Value; var digits = (match.Groups["whole"].Value + fraction).TrimStart('0'); if (digits.Length == 0) return 0;
        var negative = match.Groups["sign"].Value == "-"; if (negative && !signed) throw Invalid(); var significant = digits.TrimEnd('0'); var exponent = 0L;
        if (match.Groups["exponent"].Success && !long.TryParse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) throw Invalid();
        if (exponent is > int.MaxValue or < -int.MaxValue) throw Invalid();
        var power = exponent - fraction.Length + digits.Length - significant.Length;
        if (power < 0 || significant.Length + power > 10) throw Invalid();
        var integer = long.Parse(significant + new string('0', (int)power), CultureInfo.InvariantCulture); if (negative) integer = -integer;
        if (integer is < int.MinValue or > int.MaxValue) throw Invalid(); return (int)integer;
    }
    /// <summary>固定错误不回显原生数据或巨大数值。</summary>
    private static InvalidDataException Invalid() => new("Claude 候选索引/枚举必须是精确且范围有效的 int32。");
}
