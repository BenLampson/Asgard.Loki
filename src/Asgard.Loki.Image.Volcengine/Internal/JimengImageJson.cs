using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>Visual 原生 JSON、业务码和独立图片数组验证，不转换成方舟或 OpenAI 结果。</summary>
internal static class JimengImageJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>明确拒绝有损编码。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>连同未知字段验证字符串和重复成员，不经过浮点中间结构。</summary>
    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("Visual 图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Visual 图片 JSON 含 Undefined。");
    }
    /// <summary>严格 UTF-8 完整对象，复制原始 JSON 所有权。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Visual 图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Visual 图片 JSON 或编码无效。", error); }
    }
    /// <summary>成功码不重写为其他业务状态；签名错误和算法业务错误均保留完整正文。</summary>
    internal static void Success(JsonElement root, int status, byte[] bytes, string? type)
    {
        if (root.TryGetProperty("ResponseMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("Error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new ImageUpstreamException("Visual 图片返回原生签名/API 错误。", status, bytes, type);
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number)) throw new InvalidDataException("Visual 图片结果缺少原生整数 code。");
        if (number != 10000) throw new ImageUpstreamException("Visual 图片返回原生业务错误。", status, bytes, type);
    }
    /// <summary>两个原生数组各自保留顺序，不猜测对应关系，不生成时间戳或新 JSON 字段。</summary>
    internal static IReadOnlyList<GeneratedImage> Images(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Visual 图片成功结果缺少原生 data 对象。");
        var images = new List<GeneratedImage>();
        foreach (var name in new[] { "binary_data_base64", "image_urls" })
        {
            if (!data.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null) continue;
            if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Visual 图片载荷必须为原生数组或 null。");
            foreach (var image in array.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString())) throw new InvalidDataException("Visual 图片载荷必须是非空原生字符串。");
                images.Add(new(image, name == "image_urls" ? image.GetString() : null, name == "binary_data_base64" ? image.GetString() : null));
            }
        }
        return images.AsReadOnly();
    }
}
