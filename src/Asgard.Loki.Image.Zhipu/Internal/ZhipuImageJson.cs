using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.Zhipu.Internal;

/// <summary>智谱 v4原生字段/JSON 验证，完整保留原生图片、审核、时间及错误字段，不改写为其他供应商结构。</summary>
internal static class ZhipuImageJson
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
            foreach (var field in value.EnumerateObject()) { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("智谱 v4图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("智谱 v4图片 JSON 含 Undefined。");
    }
    /// <summary>原始请求/响应均严格解析为完整对象并复制所有权，不经过中间浮点结构。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("智谱 v4图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("智谱 v4图片原生 JSON 或编码无效。", error); }
    }
    /// <summary>保留每个原生图片项，便利视图优先使用规范字段，再取同项别名；不添加或重命名 JSON 字段。</summary>
    internal static IReadOnlyList<GeneratedImage> Images(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var items) || items.ValueKind == JsonValueKind.Null)
        {
            if (root.TryGetProperty("content_filter", out var filter) && filter.ValueKind == JsonValueKind.Array && filter.GetArrayLength() > 0
                && filter.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object)) return [];
            throw new InvalidDataException("智谱 v4 结果缺少原生 data 或明确审核信息。");
        }
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("智谱 v4 原生 data 必须为数组。");
        var images = new List<GeneratedImage>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("智谱原生图片项必须为对象。");
            var url = Value(item, "url", "image_url"); var base64 = Value(item, "b64_json", "b64_image");
            if (url is null && base64 is null) throw new InvalidDataException("智谱图片项没有原生 URL/Base64 载荷。");
            images.Add(new(item, url, base64));
        }
        return images.AsReadOnly();
    }
    /// <summary>所有已提供的已知字段均验证类型，规范字段只影响便利视图优先级，不丢弃别名或空值。</summary>
    private static string? Value(JsonElement image, params string[] names)
    {
        string? selected = null;
        foreach (var name in names)
        {
            if (!image.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("智谱原生图片载荷必须为字符串或 null。");
            if (selected is null && !string.IsNullOrWhiteSpace(value.GetString())) selected = value.GetString();
        }
        return selected;
    }
}
