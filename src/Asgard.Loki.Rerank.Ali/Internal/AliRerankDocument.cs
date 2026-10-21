using System.Text.Json;

namespace Asgard.Loki.Rerank.Ali.Internal;

/// <summary>识别原生单模态文档，不解释 URL、Data URI、图片或视频内容。</summary>
internal static class AliRerankDocument
{
    /// <summary>查询接受文本或图片；候选文档另接受视频，保留原始字符串与字典外壳。</summary>
    internal static bool TryContent(JsonElement value, out string kind, out string content, bool query = false)
    {
        kind = "text"; content = "";
        if (value.ValueKind == JsonValueKind.String) content = value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new[] { "text", "image", "video" }.Where(n => value.TryGetProperty(n, out _)).ToArray();
            if (fields.Length != 1 || query && fields[0] == "video") return false;
            kind = fields[0]; var field = value.GetProperty(kind);
            if (field.ValueKind != JsonValueKind.String) return false;
            content = field.GetString()!;
        }
        return content.Length != 0;
    }
}
