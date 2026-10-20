using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.MiniMax.Internal;

/// <summary>原生语音 JSON 的字段、数值和无损 Unicode 边界。</summary>
internal static class MiniMaxSpeechJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝无效代理项，不做替换编码。</summary>
    internal static void Text(string text) => _ = Utf8.GetByteCount(text);
    /// <summary>递归校验原始未知字段及重复键。</summary>
    internal static void Validate(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String) Text(root.GetString()!);
        else if (root.ValueKind == JsonValueKind.Array) foreach (var item in root.EnumerateArray()) Validate(item);
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            { Text(member.Name); if (!names.Add(member.Name)) throw new InvalidDataException("原生语音 JSON 含重复字段。"); Validate(member.Value); }
        }
        else if (root.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("原生语音 JSON 含未定义值。");
    }
    /// <summary>严格读取完整原生对象，复制所有权。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生语音结果必须为对象。");
            Validate(document.RootElement); return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("原生语音 JSON 或 UTF-8 编码无效。", error); }
    }
    /// <summary>校验公共桥接可变节点中的原始字符串。</summary>
    internal static void Node(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var member in obj) { Text(member.Key); Node(member.Value); }
        else if (node is JsonArray array) foreach (var child in array) Node(child);
        else if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) Text(value.GetValue<string>());
    }
    /// <summary>拒绝可能覆盖受控字段的大小写别名。</summary>
    internal static void Aliases(JsonObject obj, params string[] names)
    {
        foreach (var member in obj)
            if (names.Any(name => name.Equals(member.Key, StringComparison.OrdinalIgnoreCase) && member.Key != name))
                throw new ArgumentException("原生语音受控字段不接受大小写别名。");
    }
    /// <summary>只接受原生非空字符串，不隐式转换类型。</summary>
    internal static string Required(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(member.GetString())
            ? member.GetString()! : throw new InvalidDataException("原生语音缺少有效 " + name + "。");
    /// <summary>读取完整 Int64 状态，不缩窄成 Int32。</summary>
    internal static long Number(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.Number && member.TryGetInt64(out var number)
            ? number : throw new InvalidDataException("原生语音缺少有效整数 " + name + "。");
}
