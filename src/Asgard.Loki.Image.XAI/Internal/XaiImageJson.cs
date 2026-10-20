using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.XAI.Internal;

/// <summary>xAI原生字段/JSON 验证，完整保留原生图片、修订、用量及错误字段，不改写为其他供应商结构。</summary>
internal static class XaiImageJson
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
            foreach (var field in value.EnumerateObject()) { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("xAI图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("xAI图片 JSON 含 Undefined。");
    }
    /// <summary>原始请求/响应均严格解析为完整对象并复制所有权，不经过中间浮点结构。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("xAI图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("xAI图片原生 JSON 或编码无效。", error); }
    }
    /// <summary>原生 data[] 不改写图片、MIME、修订提示词和用量；零图片仍保留原始结果。</summary>
    internal static void Result(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("xAI 图片编辑结果缺少原生 data 数组。");
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("xAI 图片编辑项必须是对象。");
            var found = false;
            foreach (var name in new[] { "url", "b64_json", "revised_prompt" })
            {
                if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("xAI 已知图片载荷必须为字符串或 null。");
                if (name != "revised_prompt") found |= !string.IsNullOrWhiteSpace(value.GetString());
            }
            if (!found) throw new InvalidDataException("xAI 图片编辑项没有原生 URL/Base64 载荷。");
        }
    }
}
