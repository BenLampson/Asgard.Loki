using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.Midjourney.Internal;

/// <summary>代理图片原生 JSON 和任务身份验证，不携带视频状态或通用业务协调。</summary>
internal static class MidjourneyImageJson
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
            { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("代理 图片 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("代理 图片 JSON 含 Undefined。");
    }
    /// <summary>严格 UTF-8 完整对象或数组，复制原始 JSON 所有权。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("代理图片原生 JSON 必须为对象或数组。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("代理 图片 JSON 或编码无效。", error); }
    }
    /// <summary>原生身份字段为非空字符串，未知状态不改写。</summary>
    internal static string RequiredText(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("代理 原生结果缺少 " + name + "。");
    /// <summary>可选原生文本只接受字符串/null，不把对象或数值强制转换。</summary>
    internal static string? OptionalText(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("代理图片文本字段类型无效：" + name);
    }
    /// <summary>任务 ID 只作为单独路径段，拒绝点段或可引入歧义的控制字符。</summary>
    internal static void TaskId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); Text(id);
        if (id is "." or ".." || id.Any(char.IsControl)) throw new ArgumentException("代理图片任务 ID 无效。");
    }
    /// <summary>冻结完整原生请求，包括未知 CLR 字符串，避免有损编码。</summary>
    internal static byte[] Request(System.Text.Json.Nodes.JsonObject parameters, int limit)
    {
        ArgumentNullException.ThrowIfNull(parameters); Node(parameters); var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters); _ = Parse(bytes);
        if (bytes.Length > limit) throw new ArgumentException("代理图片原生 JSON 超过本地字节上限。"); return bytes;
    }
    private static void Node(System.Text.Json.Nodes.JsonNode? value)
    {
        if (value is System.Text.Json.Nodes.JsonObject obj) foreach (var field in obj) { Text(field.Key); Node(field.Value); }
        else if (value is System.Text.Json.Nodes.JsonArray array) foreach (var item in array) Node(item);
        else if (value is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var text)) Text(text);
    }
}
