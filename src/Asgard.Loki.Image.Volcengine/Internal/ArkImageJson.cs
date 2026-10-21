using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>方舟原生字段/JSON 验证，保留图片级失败与汇总，不能套用 OpenAI/Azure 图片完成结构。</summary>
internal static class ArkImageJson
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
            foreach (var field in value.EnumerateObject()) { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("方舟图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("方舟图片 JSON 含 Undefined。");
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
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("方舟图片原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("方舟图片原生 JSON 或编码无效。", error); }
    }
    /// <summary>已知成功项必须有原生图片载荷；图片级 error 仍属于原始结果，不丢弃其余图片。</summary>
    internal static void Image(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("方舟图片项必须是对象。");
        var found = false;
        foreach (var name in new[] { "url", "b64_json" })
        {
            if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("方舟图片原生载荷必须是字符串。");
            found |= !string.IsNullOrWhiteSpace(value.GetString());
        }
        if (!found) throw new InvalidDataException("方舟成功图片项缺少原生 URL/Base64。");
    }
    /// <summary>原生错误保留完整对象与未知字段，只验证必要错误身份。</summary>
    internal static void Error(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("方舟图片原生错误缺少 code/message 对象。");
    }
}
