using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.NewAPI.Internal;

/// <summary>只处理请求快照、严格 Unicode 与固定路径，不解释搜索业务字段。</summary>
internal static class NewApiSearchInput
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>禁止序列化器以替换字符掩盖输入中的无效 Unicode。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>规范根末尾分隔符，保留调用者的完整前缀，不猜测版本或部署。</summary>
    internal static Uri Root(Uri root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0 || root.OriginalString.Any(char.IsControl))
            throw new ArgumentException("搜索需要无凭据、查询、片段或控制字符的完整 HTTP(S) API 根。");
        return new(root.AbsoluteUri.TrimEnd('/')+"/");
    }
    /// <summary>选中的实际模型由目标拥有；其他字段保留省略、null、布尔值和未知结构。</summary>
    internal static byte[] Body(JsonObject parameters, string model, int maximum)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var property in parameters) if (property.Key.Equals("model", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("搜索参数不能覆盖固定目标 model。");
        var pending = new Stack<JsonNode?>(); pending.Push(parameters);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var item in array) pending.Push(item);
            else if (node is JsonValue value)
            {
                if (value.TryGetValue<JsonElement>(out var element)) Element(element);
                else if (value.TryGetValue<string>(out var text)) Text(text);
                else if (value.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array) throw new ArgumentException("原生容器必须显式使用 JsonObject/JsonArray 或 JsonElement。");
            }
        }
        var body = parameters.DeepClone().AsObject(); body["model"] = model; var json = body.ToJsonString();
        if (Utf8.GetByteCount(json) > maximum) throw new InvalidDataException("原生搜索请求超过 UTF-8 字节上限。");
        return Utf8.GetBytes(json);
    }
    /// <summary>嵌入 JSON 同样检查所有字符串、属性和重复名称，不读取业务含义。</summary>
    private static void Element(JsonElement root)
    {
        var pending = new Stack<JsonElement>(); pending.Push(root);
        try
        {
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("原生搜索不接受 Undefined JSON。");
                if (value.ValueKind == JsonValueKind.String) Text(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) pending.Push(item);
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject()) { Text(property.Name); if (!names.Add(property.Name)) throw new InvalidDataException("原生搜索 JSON 含重复属性。"); pending.Push(property.Value); }
                }
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("原生搜索 JSON 含无效 Unicode 转义。", error); }
    }
}
