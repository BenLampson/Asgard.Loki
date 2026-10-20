using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.DeepSeek.Internal;

/// <summary>严格原生 JSON/Unicode 处理；不解释页面内容、查询语法、模型或工具关联。</summary>
internal static class DeepSeekToolsJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>原始 CLR 字符串不能由序列化器静默替换非法代理字符。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>在首次克隆前遍历全部原生节点和嵌入元素，未知字段也必须无损。</summary>
    internal static void Input(JsonNode root)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue scalar)
            { if (scalar.TryGetValue<JsonElement>(out var element)) Element(element); else if (scalar.TryGetValue<string>(out var text)) Text(text); }
        }
    }
    /// <summary>原始字节以严格 UTF-8/JSON 解析，重复属性或未知字段的无效转义不能通过。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        _ = Utf8.GetCharCount(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); Element(document.RootElement); return document.RootElement.Clone();
    }
    /// <summary>只识别原生根错误封装；不把 code/message 或页面/业务内容当错误。</summary>
    internal static bool Error(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
    /// <summary>即使 JSON 文本有效，孤立代理转义也不能在克隆时被忽略。</summary>
    private static void Element(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        try
        {
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { Text(property.Name); pending.Push(property.Value); }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("DeepSeek JSON 包含无效 Unicode 转义。", error); }
    }
}
