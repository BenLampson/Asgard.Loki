using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.MiniMax.Internal;

/// <summary>MiniMax原生字段/JSON 验证，完整保留元数据、原生数组和业务状态，不改写为其他供应商结构。</summary>
internal static class MiniMaxImageJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>包括未知字段的 Unicode/重复成员验证。</summary>
    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("MiniMax图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("MiniMax图片 JSON 含 Undefined。");
    }
    /// <summary>CLR 字符串先验证，避免 JSON 编码时出现有损替代；未知自定义值再由完整 JSON 验证。</summary>
    internal static void Node(JsonNode? value)
    {
        if (value is JsonObject obj) foreach (var field in obj) { Text(field.Key); Node(field.Value); }
        else if (value is JsonArray array) foreach (var item in array) Node(item);
        else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) Text(text);
    }
    /// <summary>原始请求/响应均严格解析为完整对象并复制所有权，不经过中间浮点结构。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MiniMax图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("MiniMax图片原生 JSON 或编码无效。", error); }
    }
    /// <summary>URL/Base64 原生数组各自保持顺序，不猜测两个数组之间的图片对应关系。</summary>
    internal static IReadOnlyList<GeneratedImage> Images(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MiniMax 图片结果缺少原生 data 对象。");
        var images = new List<GeneratedImage>();
        foreach (var name in new[] { "image_urls", "image_base64" })
        {
            if (!data.TryGetProperty(name, out var array)) continue;
            if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("MiniMax 图片载荷必须为原生数组。");
            foreach (var image in array.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString())) throw new InvalidDataException("MiniMax 图片载荷必须是非空原生字符串。");
                images.Add(new(image, name == "image_urls" ? image.GetString() : null, name == "image_base64" ? image.GetString() : null));
            }
        }
        return images.AsReadOnly();
    }
}
