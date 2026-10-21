using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.DeepSeek.Internal;

/// <summary>仅验证原生输入结构与公开限制，保留缺省、空值和未知字段，不生成网关模型名。</summary>
internal static class DeepSeekToolsPayload
{
    /// <summary>同步冻结整个原生请求并按最终 UTF-8 字节限制；不填默认 count 或 need_content。</summary>
    internal static byte[] Snapshot(JsonObject parameters, bool fetch, int maximum)
    {
        ArgumentNullException.ThrowIfNull(parameters); DeepSeekToolsJson.Input(parameters);
        if (parameters.Any(p => p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Search/Fetch 仅支持完整 JSON，不接受 stream 控制字段。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters.DeepClone()); if (bytes.Length > maximum) throw new ArgumentException("DeepSeek 工具请求超过配置 UTF-8 字节上限。"); var raw = DeepSeekToolsJson.Parse(bytes);
        if (fetch)
        {
            var text = RequiredText(raw, "url", 8192);
            if (text != text.Trim() || text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.Host.Length == 0 || address.UserInfo.Length != 0) throw new ArgumentException("原生 fetch URL 必须为无用户名/密码的 HTTP(S) 页面地址。");
        }
        else
        {
            _ = RequiredText(raw, "query", 128);
            if (raw.TryGetProperty("count", out var count) && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number is < 1 or > 50)) throw new ArgumentException("原生 search count 必须为 1 到 50 的整数。");
            if (raw.TryGetProperty("ori_question", out var original) && original.ValueKind != JsonValueKind.Null) _ = RequiredText(raw, "ori_question", 128);
            if (raw.TryGetProperty("localization", out var localization) && localization.ValueKind != JsonValueKind.String) throw new ArgumentException("原生 localization 必须为字符串，不自动发现调用者地址。");
            if (raw.TryGetProperty("need_content", out var content) && content.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("原生 need_content 必须为布尔值。");
        }
        return bytes;
    }
    /// <summary>Unicode 长度按标量值计数，保留原始空白与字符，不按 UTF-16 代理单元误限。</summary>
    private static string RequiredText(JsonElement data, string name, int maximum)
    {
        if (!data.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())) throw new ArgumentException("原生 " + name + " 必须为非空白文本。");
        var value = field.GetString()!; if (value.EnumerateRunes().Count() > maximum) throw new ArgumentException("原生 " + name + " 超过公开 Unicode 字符上限。"); return value;
    }
}
