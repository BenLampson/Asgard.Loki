using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Video.Internal;

/// <summary>在快照前检查原生 JSON，避免 Unicode 替代字符修改素材引用或不透明参数。</summary>
internal static class VideoJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>只校验可无损编码，不归一化字符串。</summary>
    internal static void Text(string text) => _ = Utf8.GetByteCount(text);
    /// <summary>保留未知成员及数值原文，递归检查其字符串和属性名。</summary>
    internal static JsonObject Snapshot(JsonObject value)
    {
        ArgumentNullException.ThrowIfNull(value); var pending = new Stack<JsonNode?>(); pending.Push(value);
        while (pending.TryPop(out var item))
        {
            if (item is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (item is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (item is JsonValue scalar)
            {
                if (scalar.TryGetValue<JsonElement>(out var element)) Element(element);
                else if (scalar.TryGetValue<string>(out var text)) Text(text);
            }
        }
        return value.DeepClone().AsObject();
    }
    /// <summary>检查 DOM 中的不透明 JsonElement，不能跳过嵌套未知成员。</summary>
    private static void Element(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Element(child);
        else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { Text(property.Name); Element(property.Value); }
    }
}
