using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>Azure 图像的原生 JSON 保真边界，不改写过滤结果、计数或图像内容。</summary>
internal static class AzureImageJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝有损 Unicode 转码。</summary>
    internal static int Text(string text) => Utf8.GetByteCount(text);
    /// <summary>包含未知对象/数组的完整验证，不让重复成员覆盖成功/错误含义。</summary>
    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("Azure 图像 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
        else if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Azure 图像 JSON 含未定义值。");
    }
    /// <summary>HTTP 或原生业务错误保留实际字节；错误正文不强制为 UTF-8/JSON。</summary>
    internal static ImageGenerationResult Result(AzureImageResponse response, ImageResultMetadata metadata, Func<Exception> failure)
    {
        if ((int)response.Status is < 200 or >= 300) throw failure();
        if (response.ContentType is { } type && (!MediaTypeHeaderValue.TryParse(type, out var header)
            || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Azure 图像完整结果必须为 UTF-8 JSON。");
        JsonElement root;
        try
        {
            _ = Utf8.GetCharCount(response.Body);
            using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            root = document.RootElement; Validate(root);
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 图像结果必须是原生对象。");
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw failure();
            if (!root.TryGetProperty("data", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
                throw new InvalidDataException("Azure 图像完整成功结果缺少 data 数组。");
            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 图像项必须为对象。");
                if (image.TryGetProperty("error", out var itemError) && itemError.ValueKind != JsonValueKind.Null) throw failure();
                var found = false;
                foreach (var name in new[] { "url", "b64_json", "revised_prompt" })
                {
                    if (!image.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                    if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Azure 图像原生地址/编码/提示词必须为字符串。");
                    if (name != "revised_prompt" && !string.IsNullOrWhiteSpace(value.GetString())) found = true;
                }
                if (!found) throw new InvalidDataException("Azure 图像项缺少原生图像载荷。");
            }
            return new(root, metadata);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException or EncoderFallbackException)
        { throw new InvalidDataException("Azure 图像原生 JSON 或编码无效。", error); }
    }
}
