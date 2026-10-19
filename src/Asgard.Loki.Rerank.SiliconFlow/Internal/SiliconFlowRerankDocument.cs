using System.Text.Json;

namespace Asgard.Loki.Rerank.SiliconFlow.Internal;

/// <summary>读取已核对的文本/图片内容，不下载引用、推断 MIME 或展开文档对象。</summary>
internal static class SiliconFlowRerankDocument
{
    /// <summary>查询为非空文本或图片对象；文档保留原生字符串/TextDoc/ImageDoc，允许没有最小长度约束的文本。</summary>
    internal static bool TryContent(JsonElement value, out string kind, out string content, bool query = false)
    {
        kind = "text"; content = "";
        if (value.ValueKind == JsonValueKind.String) content = value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new[] { "text", "image", "video" }.Where(name => value.TryGetProperty(name, out _)).ToArray();
            if (fields.Length != 1 || fields[0] == "video" || query && fields[0] != "image") return false;
            kind = fields[0]; var field = value.GetProperty(kind);
            if (field.ValueKind != JsonValueKind.String) return false;
            content = field.GetString()!;
        }
        else return false;
        return (!query && kind == "text") || content.Length != 0;
    }
}
