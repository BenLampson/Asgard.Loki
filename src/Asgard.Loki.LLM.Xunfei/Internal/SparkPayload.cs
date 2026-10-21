using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Xunfei.Internal;

/// <summary>原生 JSON 快照仅注入固定 AppId，保留全部原生消息与设置，不转换角色或历史。</summary>
internal static class SparkPayload
{
    /// <summary>首次凭据等待前完成深拷贝、控制字段及原生基本形状检查，最后按实际字节限额。</summary>
    internal static byte[] Snapshot(JsonObject request, string model, XunfeiSparkOptions options)
    {
        ArgumentNullException.ThrowIfNull(request); SparkJson.Input(request); var original = SparkJson.Parse(JsonSerializer.SerializeToUtf8Bytes(request));
        foreach (var property in original.EnumerateObject())
            if (property.Name.Equals("model", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("stream", StringComparison.OrdinalIgnoreCase)
                || new[] { "header", "parameter", "payload" }.Any(n => n.Equals(property.Name, StringComparison.OrdinalIgnoreCase) && n != property.Name)) throw new ArgumentException("星火原生正文不能含根 model/stream 或混淆大小写。");
        var copy = JsonNode.Parse(original.GetRawText())!.AsObject();
        JsonObject header;
        if (!copy.TryGetPropertyValue("header", out var node)) { header = new(); copy.Add("header", header); }
        else header = node as JsonObject ?? throw new ArgumentException("星火 header 必须为对象。");
        if (header.Any(p => p.Key.Equals("app_id", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("星火 app_id 由固定供应商配置拥有。");
        if (copy["parameter"] is not JsonObject parameter || parameter["chat"] is not JsonObject chat || chat["domain"] is not JsonValue domain || !domain.TryGetValue<string>(out var actual) || actual != model
            || parameter.Any(p => p.Key.Equals("chat", StringComparison.OrdinalIgnoreCase) && p.Key != "chat") || chat.Any(p => p.Key.Equals("domain", StringComparison.OrdinalIgnoreCase) && p.Key != "domain")) throw new ArgumentException("星火 domain 必须与已绑定实际模型一致。");
        if (copy["payload"] is not JsonObject payload || payload["message"] is not JsonObject message || message["text"] is not JsonArray text || text.Count == 0) throw new ArgumentException("星火原生 payload.message.text 必须为非空消息数组。");
        foreach (var item in text)
            if (item is not JsonObject entry || entry["role"] is not JsonValue role || !role.TryGetValue<string>(out var r) || string.IsNullOrEmpty(r) || entry["content"] is not JsonValue content || !content.TryGetValue<string>(out _)) throw new ArgumentException("星火原生消息必须具有明确的 role/content 文本。");
        header.Add("app_id", options.AppId); var body = JsonSerializer.SerializeToUtf8Bytes(copy);
        if (body.Length > options.MaximumRequestBytes) throw new ArgumentException("星火原生请求超过最终字节上限。"); return body;
    }
}
