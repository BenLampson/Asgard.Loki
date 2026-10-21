using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Cloudflare.Internal;

/// <summary>固定原生调用目标、模型 URL 与认证，不猜测模型格式或补默认生成参数。</summary>
internal static class CloudflareTextRequest
{
    /// <summary>在首次工厂创建/等待前冻结全部原生参数并验证原始字符串、路径和字节预算。</summary>
    internal static HttpRequestMessage Create(JsonObject payload, LlmAdapterContext context, CloudflareTextOptions options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root); var model = context.Target.UpstreamModel; ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!root.IsAbsoluteUri || root.Scheme is not ("https" or "http") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Workers AI 原生运行根地址必须为无凭据、查询或片段的 HTTP(S) 绝对地址。");
        if (context.Credential.Kind != LlmCredentialKind.ApiKey) throw new ArgumentException("Workers AI 原生文本仅支持已选 API Token Bearer。");
        CloudflareJson.Text(model); CloudflareJson.Input(payload);
        if (payload.Any(p => p.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("stream", StringComparison.OrdinalIgnoreCase) && p.Key != "stream"))
            throw new ArgumentException("Workers AI 模型由已绑定 URL 拥有，原生 stream 不能使用大小写别名。");
        var prompt = payload["prompt"] is JsonValue value && value.TryGetValue<string>(out _);
        var messages = payload["messages"] is JsonArray array && array.All(item => item is JsonObject);
        if (!prompt && !messages) throw new ArgumentException("Workers AI 原生文本需要 prompt 字符串或 messages 对象数组。");
        var segments = model.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or "..")) throw new ArgumentException("Workers AI 原生模型路径不能含空段或点路径段。");
        var path = string.Join('/', segments.Select(s => Uri.EscapeDataString(s).Replace("%40", "@", StringComparison.Ordinal)));
        var endpoint = new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path);
        var body = payload.DeepClone().AsObject(); body["stream"] = stream; var text = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true);
        if (utf8.GetByteCount(text) > options.MaximumRequestBytes) throw new InvalidDataException("Workers AI 原生请求超过 UTF-8 字节预算。");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.ApiKey); request.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
            request.Content = new ByteArrayContent(utf8.GetBytes(text)); request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; return request;
        }
        catch { request.Dispose(); throw; }
    }
    /// <summary>选中 Bearer 覆盖默认 Authorization，不继承另一认证或未绑定的 AI Gateway 头。</summary>
    internal static void Defaults(HttpClient client)
    {
        if (new[] { "api-key", "x-api-key", "x-goog-api-key" }.Any(client.DefaultRequestHeaders.Contains)
            || client.DefaultRequestHeaders.Any(h => h.Key.StartsWith("cf-aig-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Workers AI 原生调用含未绑定的认证或 Gateway 默认头。");
    }
    /// <summary>捕获的字面 URI 与方法在读取和资源释放后仍必须保持原有完整目标。</summary>
    internal static void Verify(HttpRequestMessage request, HttpResponseMessage response, string endpoint)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.OriginalString != endpoint
            || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != endpoint))
            throw new InvalidDataException("Workers AI 原生调用的实际方法或目标发生变化。");
    }
}
