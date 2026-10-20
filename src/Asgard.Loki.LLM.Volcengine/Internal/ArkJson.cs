using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Volcengine.Internal;

/// <summary>方舟原生 JSON/Unicode 校验，不转换工具/推理或解释业务嵌套对象。</summary>
internal static class ArkJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>原始 CLR 字符串和嵌入元素先校验，避免克隆/序列化器替换非法代理字符。</summary>
    internal static void Input(JsonNode root)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue value)
            { if (value.TryGetValue<JsonElement>(out var element)) Element(element); else if (value.TryGetValue<string>(out var text)) Text(text); }
        }
    }
    /// <summary>严格 UTF-8、重复属性和未知节点转义，返回独立 JSON 所有权。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try { _ = Utf8.GetCharCount(bytes.Span); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("方舟原生正文不是有效 UTF-8。", error); }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); Element(document.RootElement); return document.RootElement.Clone();
    }
    /// <summary>遍历未知元素也验证属性名、重复值和完整 Unicode。</summary>
    private static void Element(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        try
        {
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("方舟原生 JSON 不能包含 Undefined。");
                if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject()) { Text(property.Name); if (!names.Add(property.Name)) throw new InvalidDataException("方舟原生 JSON 包含重复属性。"); pending.Push(property.Value); }
                }
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("方舟原生 JSON 包含无效字符串转义。", error); }
    }
    /// <summary>只识别原生根错误，不把工具/插件结果的业务错误当作 HTTP 调用失败。</summary>
    internal static bool Error(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
