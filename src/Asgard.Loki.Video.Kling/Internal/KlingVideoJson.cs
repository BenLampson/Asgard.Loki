using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Video.Kling.Internal;

/// <summary>可灵原生 JSON、任务标识及文本边界，不携带其他能力的状态或转换。</summary>
internal static class KlingVideoJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝有损编码，不改变原生标识或内容。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>任务 ID 作为单独路径段发送，拒绝可被 URI 规范化的跳转标识。</summary>
    internal static void TaskId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value);
        if (value is "." or ".." || value.Any(char.IsControl)) throw new ArgumentException("可灵任务标识无效。");
    }
    /// <summary>严格校验整个原生 JSON，包括未知成员、重复字段和字符串转义。</summary>
    internal static JsonElement Parse(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("可灵接口没有返回原生 JSON。");
        try
        {
            _ = Utf8.GetCharCount(bytes); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("可灵任务结果必须是原生对象。");
            var pending = new Stack<JsonElement>(); pending.Push(root);
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var member in value.EnumerateObject()) { _ = member.Name; pending.Push(member.Value); }
            }
            return root.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("可灵原生 JSON 或 UTF-8 编码无效。", error); }
    }
    /// <summary>原生身份及状态必须保持非空文本，不能强制字符串化。</summary>
    internal static string RequiredText(JsonElement value, string name)
        => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()! : throw new InvalidDataException("可灵结果缺少有效 " + name + "。");
}
