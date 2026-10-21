using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Rerank.OpenAICompatible.Internal;

/// <summary>本供应商原生 JSON/Unicode 验证，不重写数值、未知字段或向量结构。</summary>
internal static class OpenAiCompatibleRerankJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>明确拒绝无效 CLR Unicode 的有损编码。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>未知字段也验证字符串和唯一成员，不经过浮点中间结果。</summary>
    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { Text(field.Name); if (!names.Add(field.Name)) throw new JsonException("原生重排 JSON 含重复字段。"); Validate(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("原生重排 JSON 含 Undefined。");
    }
    /// <summary>验证完整原生响应或错误对象；编码错误明确失败，不替换字符。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        try { _ = Utf8.GetCharCount(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("原生重排结果不是有效 UTF-8。", error); }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生重排结果必须为完整 JSON 对象。");
        try { Validate(document.RootElement); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("原生重排 JSON Unicode 无效。", error); }
        return document.RootElement.Clone();
    }
}
