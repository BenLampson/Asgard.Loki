using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.GoogleVertex.Internal;

/// <summary>Gemini原生字段/JSON 验证，保留完整候选、签名和安全结果，不改写为其他供应商结构。</summary>
internal static class VertexImageJson
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
            foreach (var field in value.EnumerateObject()) { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("Gemini图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Gemini图片 JSON 含 Undefined。");
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
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Gemini图片原生 JSON 或编码无效。", error); }
    }
    /// <summary>只为原生 image MIME 的内联载荷提供图像视图，原始签名和全部零图片结果仍保留。</summary>
    internal static IReadOnlyList<GeneratedImage> Images(JsonElement root)
    {
        var images = new List<GeneratedImage>();
        if (!root.TryGetProperty("candidates", out var candidates)) return images;
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)) continue;
            if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Gemini 候选 content 必须带原生 parts 数组。");
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini 原生 part 必须为对象。");
                var camel = part.TryGetProperty("inlineData", out var inline); var snake = part.TryGetProperty("inline_data", out var alternate);
                if (camel && snake) throw new InvalidDataException("Gemini 图像项不能重复提供 inlineData 别名。");
                if (!camel && !snake) continue;
                if (snake) inline = alternate;
                if (inline.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini inlineData 必须为对象。");
                var camelMime = inline.TryGetProperty("mimeType", out var mime); var snakeMime = inline.TryGetProperty("mime_type", out var otherMime);
                if (camelMime && snakeMime) throw new InvalidDataException("Gemini MIME 字段不能重复别名。");
                if (snakeMime) mime = otherMime;
                if ((!camelMime && !snakeMime) || mime.ValueKind != JsonValueKind.String) throw new InvalidDataException("Gemini 内联 MIME 缺失。");
                if (!mime.GetString()!.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!inline.TryGetProperty("data", out var bytes) || bytes.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(bytes.GetString()))
                    throw new InvalidDataException("Gemini 内联图片缺少原生 data。");
                images.Add(new(part, null, bytes.GetString()) { ContentType = mime.GetString() });
            }
        }
        return images.AsReadOnly();
    }
    /// <summary>只识别根级原生 API error，不检查嵌套工具业务字段。</summary>
    internal static bool Error(JsonElement root) => root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
}
