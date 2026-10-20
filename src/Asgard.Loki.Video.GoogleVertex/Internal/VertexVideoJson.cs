using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Video.GoogleVertex.Internal;

/// <summary>供应商资源名与严格原生 JSON 边界，不携带其他能力的业务状态。</summary>
internal static class VertexVideoJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝有损 Unicode 文本。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>原生资源路径段不接受查询、转义路径或路径跳转。</summary>
    internal static void Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value);
        if (value is "." or ".." || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\%?#:".Contains(c)))
            throw new ArgumentException("Vertex 资源路径段无效。");
    }
    /// <summary>视频目标只支持显式 Google publisher 资源，不猜测模型、项目、区域或 endpoint。</summary>
    internal static string Resource(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource); Text(resource); var parts = resource.Split('/');
        if (parts.Length != 8 || parts[0] != "projects" || parts[2] != "locations" || parts[4] != "publishers" || parts[5] != "google" || parts[6] != "models")
            throw new ArgumentException("Vertex 视频要求完整 Google publisher 模型资源名。");
        foreach (var part in parts) Segment(part); return string.Join('/', parts.Select(Uri.EscapeDataString));
    }
    /// <summary>完整操作名称必须属于同一项目、区域、publisher 和实际模型。</summary>
    internal static string OperationName(string name, string modelResource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); Text(name); var prefix = modelResource + "/operations/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Vertex 操作名称不属于当前完整目标。");
        Segment(name[prefix.Length..]); return name;
    }
    /// <summary>严格解析整个原生对象，未知成员同样拒绝重复属性、无效字符串或无效 UTF-8。</summary>
    internal static JsonElement Parse(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header)
            || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Vertex 视频接口未返回原生 JSON。");
        try
        {
            _ = Utf8.GetCharCount(bytes); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex 操作必须是原生 JSON 对象。");
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
        { throw new InvalidDataException("Vertex 原生 JSON 或 UTF-8 编码无效。", error); }
    }
}
