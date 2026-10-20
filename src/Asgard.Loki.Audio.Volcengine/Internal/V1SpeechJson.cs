using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Volcengine.Internal;

/// <summary>供应商原生 JSON 的严格编码及字段边界。</summary>
internal static class V1SpeechJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>验证字符串不含无效代理项。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>严格解析任意原生 JSON，拒绝重复字段及损坏的未知字符串。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            Validate(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("V1 原生 JSON 编码或结构无效。", error); }
    }
    /// <summary>递归读取原始 JSON 中的字符串及键，避免序列化替换无效 Unicode。</summary>
    internal static void Validate(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String) Text(root.GetString()!);
        else if (root.ValueKind == JsonValueKind.Array) foreach (var item in root.EnumerateArray()) Validate(item);
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                Text(member.Name);
                if (!names.Add(member.Name)) throw new InvalidDataException("V1 原生 JSON 含重复字段。");
                Validate(member.Value);
            }
        }
        else if (root.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("V1 原生 JSON 含未定义值。");
    }
    /// <summary>验证公共请求的可变节点，包括直接构造的字符串。</summary>
    internal static void ValidateNode(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var member in obj) { Text(member.Key); ValidateNode(member.Value); }
        else if (node is JsonArray array) foreach (var child in array) ValidateNode(child);
        else if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) Text(value.GetValue<string>());
    }
    /// <summary>仅接受规范拼写的受控字段，避免大小写别名覆盖。</summary>
    internal static void Aliases(JsonObject value, params string[] names)
    {
        foreach (var member in value)
            if (names.Any(name => name.Equals(member.Key, StringComparison.OrdinalIgnoreCase) && name != member.Key))
                throw new ArgumentException("V1 受控字段不能使用大小写别名。");
    }
    /// <summary>必填非空字符串，不转换数字或对象。</summary>
    internal static string Required(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(member.GetString())
            ? member.GetString()! : throw new InvalidDataException("V1 缺少有效 " + name + "。");
}
