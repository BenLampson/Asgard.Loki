using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Cohere.Internal;

/// <summary>明确原生目标、认证及方法字段；不选择模型、填充默认生成设置或转换工具。</summary>
internal static class CohereRequest
{
    /// <summary>首次等待和工厂创建前完成原始 Unicode、请求快照与字节预算验证。</summary>
    internal static HttpRequestMessage Create(JsonObject payload, LlmAdapterContext context, CohereLlmOptions options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        var target = context.Target; var root = target.BaseAddress; ArgumentNullException.ThrowIfNull(root); ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel);
        if (!root.IsAbsoluteUri || root.Scheme is not ("https" or "http") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Cohere API 根地址必须是无凭据、查询或片段的 HTTP(S) 绝对地址。");
        if (context.Credential.Kind != LlmCredentialKind.ApiKey) throw new ArgumentException("Cohere 原生调用仅支持明确选择的 API Key。");
        CohereJson.Text(target.UpstreamModel); CohereJson.Input(payload);
        foreach (var property in payload)
            if ((property.Key.Equals("model", StringComparison.OrdinalIgnoreCase) && property.Key != "model")
                || (property.Key.Equals("stream", StringComparison.OrdinalIgnoreCase) && property.Key != "stream")) throw new ArgumentException("Cohere 原生 model/stream 字段不允许大小写别名。");
        if (payload["message"] is not JsonValue message || !message.TryGetValue<string>(out _)) throw new ArgumentException("Cohere v1 message 必须是原生字符串，空字符串原样传递。");
        var body = payload.DeepClone().AsObject(); body["model"] = target.UpstreamModel; body["stream"] = stream;
        var text = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true);
        if (utf8.GetByteCount(text) > options.MaximumRequestBytes) throw new InvalidDataException("Cohere 请求超过 UTF-8 字节上限。");
        var endpoint = new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), "chat");
        var result = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            result.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.ApiKey); result.Headers.Accept.ParseAdd("application/json");
            if (options.ClientName is { } name) result.Headers.Add("X-Client-Name", name);
            result.Content = new ByteArrayContent(utf8.GetBytes(text)); result.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; return result;
        }
        catch { result.Dispose(); throw; }
    }
    /// <summary>默认认证、SSE 协商及项目名不能改变已绑定调用；选中 Bearer 覆盖默认 Authorization。</summary>
    internal static void Defaults(HttpClient client, CohereLlmOptions options)
    {
        if (new[] { "api-key", "x-api-key", "x-goog-api-key", "Accepts" }.Any(client.DefaultRequestHeaders.Contains)
            || options.ClientName is null && client.DefaultRequestHeaders.Contains("X-Client-Name")) throw new InvalidOperationException("Cohere HTTP 工厂含未绑定的认证、SSE 或项目默认头。");
    }
    /// <summary>检查实际请求及响应关联请求，没有重定向、方法变化或隐式目标替换。</summary>
    internal static void Verify(HttpRequestMessage request, HttpResponseMessage response, string endpoint)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.OriginalString != endpoint
            || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != endpoint))
            throw new InvalidDataException("Cohere 实际调用方法或目标发生改变。");
    }
}
