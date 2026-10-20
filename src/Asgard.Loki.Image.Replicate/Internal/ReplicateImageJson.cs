using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.Replicate.Internal;

/// <summary>Replicate 原生预测和模型输入上传 JSON、图片字符串视图，不改写为其他协议。</summary>
internal static class ReplicateImageJson
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
            { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("Replicate 图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Replicate 图片 JSON 含 Undefined。");
    }
    /// <summary>严格 UTF-8 完整对象，复制原始 JSON 所有权。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Replicate 图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Replicate 图片 JSON 或编码无效。", error); }
    }
    /// <summary>原生身份字段为非空字符串，未知状态不改写。</summary>
    internal static string RequiredText(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("Replicate 原生结果缺少 " + name + "。");
    /// <summary>标量或数组字符串均保留为原生项；其他模型专属输出保留在 Data，不猜测图片结构。</summary>
    internal static IReadOnlyList<GeneratedImage> Images(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind == JsonValueKind.Null) return [];
        var images = new List<GeneratedImage>();
        if (output.ValueKind == JsonValueKind.String) Add(output);
        else if (output.ValueKind == JsonValueKind.Array) foreach (var item in output.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) Add(item);
        return images.AsReadOnly();
        void Add(JsonElement item)
        { if (!string.IsNullOrWhiteSpace(item.GetString())) images.Add(new(item, item.GetString(), null)); }
    }
    /// <summary>模型输入引用原文保留，只验证绝对 HTTP(S) URL，避免把任意返回正文当成本地文件。</summary>
    internal static string UploadUrl(JsonElement root)
    {
        if (!root.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Replicate 输入上传缺少 urls 对象。");
        var url = RequiredText(urls, "get");
        if (url.Any(char.IsWhiteSpace) || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) throw new InvalidDataException("Replicate 输入上传缺少有效原生 URL。");
        return url;
    }
}
