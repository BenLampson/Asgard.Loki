using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Perplexity.Internal;

/// <summary>原生 JSON 的严格字节和 Unicode 边界，不改变未知值或供应商数据。</summary>
internal static class PerplexityJson
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>不允许序列化器用替换字符掩盖原始孤立代理字符。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>克隆之前检查原生 DOM、嵌入元素和容器，避免先丢失原文再验证。</summary>
    internal static void Input(JsonNode root)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var item in array) pending.Push(item);
            else if (node is JsonValue value)
            {
                if (value.TryGetValue<JsonElement>(out var embedded)) Element(embedded);
                else if (value.TryGetValue<string>(out var text)) Text(text);
                else if (value.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array) throw new ArgumentException("原生容器必须显式使用 JsonObject/JsonArray。");
            }
        }
    }
    /// <summary>完整原生结果和每个 SSE JSON 都拒绝非法字节、重复字段和无效转义。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try { _ = Utf8.GetCharCount(bytes.Span); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Perplexity JSON 不是有效 UTF-8。", error); }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); Element(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Perplexity 原生结果必须是 JSON 对象。");
        return document.RootElement.Clone();
    }
    /// <summary>嵌入 JsonElement 同样检查重复名称与 Unicode，不遍历业务含义。</summary>
    private static void Element(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        try
        {
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) pending.Push(item);
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject()) { Text(property.Name); if (!names.Add(property.Name)) throw new InvalidDataException("Perplexity JSON 包含重复属性。"); pending.Push(property.Value); }
                }
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Perplexity JSON 包含无效 Unicode 转义。", error); }
    }
    /// <summary>仅识别连接错误；原生 response.failed 和完整 failed 结果仍作为协议数据返回。</summary>
    internal static bool Failure(JsonElement data, string? eventName = null)
    {
        var type = data.TryGetProperty("type", out var nativeType) ? nativeType : default;
        return eventName == "error" || type.ValueKind == JsonValueKind.String && type.GetString() == "error"
            || type.ValueKind == JsonValueKind.Undefined && !(data.TryGetProperty("object", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "response") && data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
    }
}
