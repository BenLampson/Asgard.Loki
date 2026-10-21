using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Google.Internal;

/// <summary>原生 Gemini 的严格 Unicode/JSON 快照，不修复、删除或归一化业务字段。</summary>
internal static class GoogleHttpJson
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>不允许序列化器用替换字符隐藏输入中的孤立代理字符。</summary>
    internal static void Text(string text) => _ = Utf8.GetByteCount(text);
    /// <summary>克隆之前校验 CLR 字符串、嵌入 JSON 和属性名称。</summary>
    internal static void Input(JsonNode root)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var item in array) pending.Push(item);
            else if (node is JsonValue value)
            {
                if (value.TryGetValue<JsonElement>(out var element)) Element(element);
                else if (value.TryGetValue<string>(out var text)) Text(text);
                else if (value.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array) throw new ArgumentException("Gemini 原生容器必须使用 JsonObject/JsonArray。");
            }
        }
    }
    /// <summary>完整 JSON 和原生 SSE 帧拒绝有损解码、重复字段和非法转义。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try { _ = Utf8.GetCharCount(bytes.Span); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Gemini JSON 不是有效 UTF-8。", error); }
        if (bytes.Span.StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); Element(json.RootElement);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini 原生结果必须是 JSON 对象。");
        return json.RootElement.Clone();
    }
    /// <summary>仅检查编码和对象唯一性，不遍历工具、签名或安全字段的含义。</summary>
    private static void Element(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        try
        {
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Gemini 不接受 Undefined JSON。");
                if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) pending.Push(item);
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject()) { Text(property.Name); if (!names.Add(property.Name)) throw new InvalidDataException("Gemini JSON 含重复属性。"); pending.Push(property.Value); }
                }
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Gemini JSON 含无效 Unicode 转义。", error); }
    }
}
