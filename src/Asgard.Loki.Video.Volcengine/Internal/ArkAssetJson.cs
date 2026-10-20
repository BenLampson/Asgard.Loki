using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Video.Volcengine.Internal;

/// <summary>校验有限素材操作所需的原生字段，不投影或丢弃未知成员。</summary>
internal static class ArkAssetJson
{
    /// <summary>在任何等待前验证可无损编码并复制完整原生正文。</summary>
    internal static JsonObject Snapshot(JsonObject value, string action)
    {
        ArgumentNullException.ThrowIfNull(value); var pending = new Stack<JsonNode?>(); pending.Push(value);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var member in obj) { ArkVideoHttp.Text(member.Key); pending.Push(member.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue scalar)
            {
                if (scalar.TryGetValue<JsonElement>(out var element)) _ = ArkVideoHttp.Parse(JsonSerializer.SerializeToUtf8Bytes(new { value = element }));
                else if (scalar.TryGetValue<string>(out var text)) ArkVideoHttp.Text(text);
            }
        }
        var copy = value.DeepClone().AsObject();
        if (copy.ContainsKey("ProjectName")) Text(copy, "ProjectName");
        if (action == "CreateAssetGroup") Text(copy, "Name");
        if (action == "CreateAsset") foreach (var key in new[] { "GroupId", "URL", "AssetType" }) Text(copy, key);
        if (action.StartsWith("Get", StringComparison.Ordinal) || action.StartsWith("Update", StringComparison.Ordinal) || action.StartsWith("Delete", StringComparison.Ordinal)) Text(copy, "Id");
        if (action.StartsWith("List", StringComparison.Ordinal))
        {
            if (copy["Filter"] is not JsonObject filter) throw new ArgumentException("素材列表请求必须包含原生 Filter 对象。", nameof(value));
            Text(filter, "GroupType");
            var pages = copy.ContainsKey("PageNumber") || copy.ContainsKey("PageSize");
            if (pages && (copy.ContainsKey("MaxResults") || copy.ContainsKey("NextToken"))) throw new ArgumentException("两种原生分页方式不能混用。", nameof(value));
            if (pages) { Integer(copy, "PageNumber", int.MaxValue); Integer(copy, "PageSize", 100); }
            if (copy.ContainsKey("MaxResults")) Integer(copy, "MaxResults", 100);
            if (copy.ContainsKey("NextToken")) Text(copy, "NextToken");
        }
        return copy;
    }
    /// <summary>标识、项目及分页凭证必须保持原始非空文本，不能强制字符串化。</summary>
    private static void Text(JsonObject value, string key)
    {
        if (value[key] is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("素材请求缺少有效 " + key + "。", nameof(value));
    }
    /// <summary>分页计数必须是协议范围内整数，不舍入小数或字符串。</summary>
    private static void Integer(JsonObject value, string key, int maximum)
    {
        var element = JsonSerializer.SerializeToElement(value[key]);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number) || number < 1 || number > maximum)
            throw new ArgumentException("素材分页参数无效：" + key + "。", nameof(value));
    }
}
