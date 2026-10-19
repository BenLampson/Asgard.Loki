using System.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>严格、可逆的任意文本封装，不执行代码或把 JSON 字符串当作原始参数。</summary>
internal static class CustomToolTextCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>保留空串、空白、Unicode 与 JSON 外观的文本。</summary>
    internal static string Encode(string input, int maximumBytes)
    {
        Validate(input, maximumBytes);
        return new JsonObject { ["input"] = input }.ToJsonString();
    }
    /// <summary>必须是只有 input 字符串的对象；多余键和畸形数据不能丢弃后成功。</summary>
    internal static string Decode(string arguments, int maximumBytes)
    {
        if (Utf8.GetByteCount(arguments) > 6L * maximumBytes + 64) ToolRequestCodec.Reject("$custom.arguments", "custom_tool_input_limit");
        var value = Object(JsonNode.Parse(arguments));
        if (value.Count != 1 || !value.ContainsKey("input")) ToolRequestCodec.Reject("$custom.arguments", "invalid_custom_tool_arguments");
        if (value["input"] is not JsonValue scalar || !scalar.TryGetValue<string>(out _))
            ToolRequestCodec.Reject("$custom.arguments.input", "invalid_custom_tool_arguments");
        var input = Text(value["input"]); Validate(input, maximumBytes); return input;
    }
    /// <summary>按 UTF-8 字节计数，拒绝无效 UTF-16 和越界文本，不截断或替换字符。</summary>
    internal static void Validate(string input, int maximumBytes)
    {
        if (Utf8.GetByteCount(input) > maximumBytes) ToolRequestCodec.Reject("$custom.input", "custom_tool_input_limit");
    }

    /// <summary>先校验原始字符串和大小下界，再按完整 JSON 精确计数，避免替换非法 Unicode 或先展开明显超限的文本。</summary>
    internal static void ValidateJson(JsonNode value, int maximumBytes)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(value); long minimum = 0;
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj)
            {
                minimum += 2;
                foreach (var field in obj) { minimum += 3L + Utf8.GetByteCount(field.Key); pending.Push(field.Value); }
            }
            else if (node is JsonArray array)
            {
                minimum += 2; foreach (var child in array) pending.Push(child);
            }
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) minimum += 2L + Utf8.GetByteCount(text);
            else minimum++;
            if (minimum > maximumBytes) ToolRequestCodec.Reject("$custom.input", "custom_tool_input_limit");
        }
        Validate(value.ToJsonString(), maximumBytes);
    }
}
