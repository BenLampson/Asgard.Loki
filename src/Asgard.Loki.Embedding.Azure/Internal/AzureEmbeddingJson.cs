using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Embedding.Azure.Internal;

/// <summary>Azure 嵌入完整原生 JSON/Unicode 验证，不转换数值、向量或未知字段。</summary>
internal static class AzureEmbeddingJson
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
            { Text(field.Name); if (!names.Add(field.Name)) throw new InvalidDataException("Azure 嵌入 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Azure 嵌入 JSON 含 Undefined。");
    }
    /// <summary>严格 UTF-8 完整对象，复制原始 JSON 所有权。</summary>
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 嵌入原生 JSON 必须是对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Azure 嵌入 JSON 或编码无效。", error); }
    }
}
