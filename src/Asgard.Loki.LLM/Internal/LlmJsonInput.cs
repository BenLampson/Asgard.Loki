using System.Text;

namespace Asgard.Loki.LLM.Internal;

/// <summary>在原生 DOM 快照之前校验字符串，不让序列化替代字符改变工具、正文或绑定身份。</summary>
internal static class LlmJsonInput
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>只验证 UTF-16 能否无损编码，不归一化或重写合法 Unicode。</summary>
    internal static void Text(string? value) { if (value is not null) _ = Utf8.GetByteCount(value); }
    /// <summary>协议命名字段与扩展 JSON 统一先检查，省略/null/false/数字保持原样。</summary>
    internal static void Request(string? model, JsonNode? content, Dictionary<string, JsonElement> parameters, string? previousResponseId = null)
    {
        Text(model); Text(previousResponseId); Node(content);
        foreach (var property in parameters) { Text(property.Key); Element(property.Value); }
    }
    /// <summary>遍历原始 JSON 节点和属性名，不能先调用 ToJsonString/DeepClone 来验证。</summary>
    internal static void Node(JsonNode? value)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(value);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj)
                foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue scalar)
            {
                if (scalar.TryGetValue<JsonElement>(out var element)) Element(element);
                else if (scalar.TryGetValue<string>(out var text)) Text(text);
            }
        }
    }
    /// <summary>显式解码所有原生字符串转义；未知字段也不能携带孤立代理字符或非法属性名。</summary>
    private static void Element(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        while (pending.TryPop(out var value))
        {
            if (value.ValueKind == JsonValueKind.String) Text(value.GetString());
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { Text(property.Name); pending.Push(property.Value); }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
        }
    }
}
