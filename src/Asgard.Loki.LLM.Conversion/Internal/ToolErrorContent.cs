using System.Text;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式错误格式的纯 UTF-8 与 JSON 字节保护，不共用 custom 工具业务诊断或状态。</summary>
internal static class ToolErrorContent
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>精确计数并拒绝孤立代理字符；不截断或替换错误详情。</summary>
    internal static void ValidateText(string value, int maximum)
    {
        if (Utf8.GetByteCount(value) > maximum) Limit();
    }
    /// <summary>先检查字符串/属性名和大小下界，再序列化有界完整对象，保留未知成员及精确数值。</summary>
    internal static string Json(JsonObject value, int maximum)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(value); long minimum = 0;
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj)
            {
                minimum += 2;
                foreach (var property in obj) { minimum += 3L + Utf8.GetByteCount(property.Key); pending.Push(property.Value); }
            }
            else if (node is JsonArray array) { minimum += 2; foreach (var child in array) pending.Push(child); }
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) minimum += 2L + Utf8.GetByteCount(text);
            else minimum++;
            if (minimum > maximum) Limit();
        }
        var json = value.ToJsonString(); ValidateText(json, maximum); return json;
    }
    /// <summary>资源边界不接受普通字段损耗许可，也不输出错误内容。</summary>
    private static void Limit() => throw new LlmConversionException([new("tool_result_error_limit", "$tool_result.content", "工具错误内容超过配置的 UTF-8 字节上限。")]);
}
