using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Azure.Internal;

/// <summary>整个原生 JSON 的 UTF-8、重复字段及未知值保真边界。</summary>
internal static class AzureAudioJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝有损 Unicode 编码。</summary>
    internal static int Text(string value) => Utf8.GetByteCount(value);
    /// <summary>校验原生 JSON 元素中的所有未知字符串及重复成员。</summary>
    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Validate(child);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            { Text(member.Name); if (!names.Add(member.Name)) throw new InvalidDataException("Azure 音频 JSON 含重复字段。"); Validate(member.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Azure 音频 JSON 含未定义值。");
    }
    /// <summary>严格解析原始 UTF-8 对象并复制所有权，不重构未知字段。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 音频 JSON 必须为对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("Azure 音频 JSON 或编码无效。", error); }
    }
    /// <summary>验证原生文本输出编码，正文仍按原始字节返回。</summary>
    internal static void Plain(byte[] bytes) => _ = Utf8.GetCharCount(bytes);
    /// <summary>原始可变节点也先检查 Unicode，避免序列化时替换直接构造的无效代理项。</summary>
    internal static void Node(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var member in obj) { Text(member.Key); Node(member.Value); }
        else if (node is JsonArray array) foreach (var child in array) Node(child);
        else if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) Text(value.GetValue<string>());
    }
    /// <summary>拒绝受控字段的大小写别名，但保留未知成员原始名称。</summary>
    internal static void Aliases(IEnumerable<string> keys, params string[] names)
    {
        foreach (var key in keys)
            if (names.Any(name => key.Equals(name, StringComparison.OrdinalIgnoreCase) && key != name))
                throw new ArgumentException("Azure 音频受控字段不接受大小写别名。");
    }
}
