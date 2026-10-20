using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio.Internal;

/// <summary>音频任务快照的 JSON/Unicode 保真边界，不定义供应商协议。</summary>
internal static class AudioTaskJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Validate(child);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject()) { Text(member.Name); if (!names.Add(member.Name)) throw new InvalidDataException("音频任务 JSON 字段重复。"); Validate(member.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("音频任务 JSON 含未定义值。");
    }
    private static void Node(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var member in obj) { Text(member.Key); Node(member.Value); }
        else if (node is JsonArray array) foreach (var child in array) Node(child);
        else if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) Text(value.GetValue<string>());
    }
    internal static JsonObject Snapshot(JsonObject source)
    {
        Node(source); var bytes = JsonSerializer.SerializeToUtf8Bytes(source);
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); Validate(parsed.RootElement);
        return JsonNode.Parse(parsed.RootElement.GetRawText())!.AsObject();
    }
}
