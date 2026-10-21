using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Ali.Internal;

/// <summary>阿里原生字段/JSON 验证，保留图片级失败与汇总，不能套用 OpenAI/Azure 图片完成结构。</summary>
internal static class AliImageJson
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
            foreach (var field in value.EnumerateObject()) { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("阿里图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("阿里图片 JSON 含 Undefined。");
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
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("阿里图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("阿里图片原生 JSON 或编码无效。", error); }
    }
    /// <summary>图像视图仅提取明确的原生 image/url 字段，保留原始布局。</summary>
    internal static IReadOnlyList<GeneratedImage> Images(JsonElement root)
    {
        var result = new List<GeneratedImage>();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object) return result;
        if (output.TryGetProperty("choices", out var choices) && choices.ValueKind != JsonValueKind.Null)
        {
            if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException("阿里 choices 必须为原生数组。");
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) throw new InvalidDataException("阿里 choice 缺少原生 message/content。");
                foreach (var part in content.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("image", out var image))
                    { if (image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString())) throw new InvalidDataException("阿里图片载荷必须为非空原生字符串。"); result.Add(new(part, image.GetString(), null)); }
            }
        }
        if (output.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var part in results.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("url", out var url))
                { if (url.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(url.GetString())) throw new InvalidDataException("阿里图片 URL 必须为非空原生字符串。"); result.Add(new(part, url.GetString(), null)); }
        return result.AsReadOnly();
    }
    internal static bool Error(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || code.ValueKind == JsonValueKind.Null) return false;
        if (code.ValueKind != JsonValueKind.String) throw new InvalidDataException("阿里原生 code 必须为字符串或 null。");
        return !string.IsNullOrEmpty(code.GetString());
    }
}
