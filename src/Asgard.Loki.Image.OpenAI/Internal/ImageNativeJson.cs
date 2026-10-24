using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>原生 ImagesResponse 容器的纯结构校验；生成/编辑分别提供自己的错误类型，不共享业务状态。</summary>
internal static class ImageNativeJson
{
    /// <summary>不重构图片、格式和用量；未知数值/字段按原生 JSON 保留。</summary>
    internal static ImageGenerationResult Result(HttpResponseMessage response, byte[] body, ImageResultMetadata metadata, Func<Exception> failure)
    {
        if (!response.IsSuccessStatusCode) throw failure();
        var text = new UTF8Encoding(false, true).GetString(body);
        using var document = JsonDocument.Parse(text.StartsWith('\uFEFF') ? text[1..] : text); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生图像 JSON 必须为对象。");
        if (root.EnumerateObject().Any(p => p.Name == "error" && p.Value.ValueKind != JsonValueKind.Null)) throw failure();
        Unique(root);
        if (!root.TryGetProperty("data", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            throw new InvalidDataException("原生图片结果缺少非空 data 数组。");
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object) throw new InvalidDataException("图片结果必须为对象。");
            var found = false;
            foreach (var name in new[] { "b64_json", "url", "revised_prompt" })
            {
                if (!image.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("已知原生图片字段必须为字符串。");
                if (name != "revised_prompt" && !string.IsNullOrWhiteSpace(value.GetString())) found = true;
            }
            if (!found) throw new InvalidDataException("图片结果缺少原生 b64_json/url。");
        }
        return new(root, metadata) { RawResponse = new((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString()) };
    }
    /// <summary>拒绝重复成员和无效字符串，不按末值覆盖输入/成功/错误含义。</summary>
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("原生图像 JSON 含重复成员。"); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
    }
}
