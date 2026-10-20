using System.Text;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Volcengine.Internal;

/// <summary>固定原生目标/认证的独立请求快照，不按模型前后缀或主机推断接口。</summary>
internal static class ArkRequest
{
    /// <summary>新策略在构造时完整检查，随后保留不可变快照。</summary>
    internal static VolcengineHttpOptions Options(VolcengineHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBodyBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumTrackedChoices, 0); return options with { };
    }
    /// <summary>首个工厂/异步边界之前检查原始参数，再冻结实际模型/方法流标志和全部未知值。</summary>
    internal static HttpRequestMessage Create(Uri root, string path, string apiKey, JsonObject payload, string model, bool stream, VolcengineHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(payload); ArgumentException.ThrowIfNullOrWhiteSpace(model); ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("方舟 API 根必须是无凭据、查询或片段的 HTTP(S) 绝对地址。");
        ArkJson.Text(model); ArkJson.Text(apiKey); if (apiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("方舟 API Key 需要无空白/控制字符的 ASCII 内容。");
        ArkJson.Input(payload);
        foreach (var key in payload.Select(p => p.Key))
            if (new[] { "model", "stream" }.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase) && n != key)) throw new ArgumentException("方舟原生 model/stream 别名冲突。");
        var body = payload.DeepClone().AsObject(); body["model"] = model; body["stream"] = stream; var text = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true);
        if (utf8.GetByteCount(text) > options.MaximumRequestBytes) throw new InvalidDataException("方舟原生请求超过 UTF-8 字节预算。");
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path));
        try
        {
            message.Headers.Host = root.Authority; message.Headers.Authorization = new("Bearer", apiKey); message.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
            message.Content = new ByteArrayContent(utf8.GetBytes(text)); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; return message;
        }
        catch { message.Dispose(); throw; }
    }
}
