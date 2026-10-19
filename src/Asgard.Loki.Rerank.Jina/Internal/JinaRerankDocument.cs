using System.Text.Json;

namespace Asgard.Loki.Rerank.Jina.Internal;

/// <summary>识别原生字符串、TextDoc 和 ImageDoc，不解码媒体、转换容器或读取外部地址。</summary>
internal static class JinaRerankDocument
{
    /// <summary>查询可使用字符串或 ImageDoc；文档另可使用 TextDoc。歧义双内容不静默选边。</summary>
    internal static bool TryContent(JsonElement value, out string kind, out string content, bool query = false)
    {
        kind = "text"; content = "";
        if (value.ValueKind == JsonValueKind.String) { content = value.GetString()!; return true; }
        if (value.ValueKind != JsonValueKind.Object) return false;
        var hasText = value.TryGetProperty("text", out var text); var hasImage = value.TryGetProperty("image", out var image);
        if (hasText == hasImage || query && hasText) return false;
        var selected = hasText ? text : image;
        if (selected.ValueKind != JsonValueKind.String) return false;
        kind = hasText ? "text" : "image"; content = selected.GetString()!; return true;
    }
}
