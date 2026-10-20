using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Perplexity.Internal;

/// <summary>冻结单次原生请求，完整目标与选中认证贯穿发送和关闭检查。</summary>
internal sealed class PerplexityCall : IDisposable
{
    private readonly string _endpoint;
    private readonly string _host;
    private readonly string _key;
    private readonly string _accept;
    private readonly HttpContent _content;
    internal HttpRequestMessage Request { get; }
    /// <summary>在工厂创建前完成原始 Unicode、参数及最终字节预算校验。</summary>
    internal PerplexityCall(JsonObject payload, LlmAdapterContext context, bool stream, int maximum, string path = "responses", bool responses = true)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("https" or "http") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("Perplexity 需要无凭据、查询或片段的 HTTP(S) 完整根。");
        if (context.Credential.Kind != LlmCredentialKind.ApiKey) throw new NotSupportedException("Perplexity 原生调用需要显式 API Key。");
        _key = context.Credential.ApiKey; if (_key.Any(c => c is < '!' or > '~')) throw new ArgumentException("Perplexity API Key 必须为无空白的 ASCII 文本。");
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Target.UpstreamModel); PerplexityJson.Text(context.Target.UpstreamModel); PerplexityJson.Input(payload);
        foreach (var property in payload) if (new[] { "model", "stream" }.Any(name => name.Equals(property.Key, StringComparison.OrdinalIgnoreCase) && name != property.Key)) throw new ArgumentException("Perplexity model/stream 不允许大小写别名。");
        if (responses)
        {
            foreach (var property in payload)
                if (new[] { "models", "preset", "profile" }.Contains(property.Key, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException("Perplexity 完整目标不允许请求覆盖模型选择或隐式回退链。");
            if (payload["input"] is not JsonArray && !(payload["input"] is JsonValue input && input.TryGetValue<string>(out _))) throw new ArgumentException("Perplexity input 必须是原生字符串或数组。");
        }
        else if (payload["messages"] is not JsonArray) throw new ArgumentException("Perplexity Sonar messages 必须是原生数组。");
        var body = payload.DeepClone().AsObject(); body["model"] = context.Target.UpstreamModel; body["stream"] = stream;
        var text = body.ToJsonString(); if (PerplexityJson.Utf8.GetByteCount(text) > maximum) throw new InvalidDataException("Perplexity 请求超过 UTF-8 字节上限。");
        _endpoint = root.AbsoluteUri.TrimEnd('/')+"/"+path; _host = root.Authority; _accept = stream ? "text/event-stream" : "application/json";
        Request = new(HttpMethod.Post, _endpoint); _content = new ByteArrayContent(PerplexityJson.Utf8.GetBytes(text));
        try { Request.Content = _content; _content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; Request.Headers.Host = _host; Request.Headers.Authorization = new("Bearer", _key); Request.Headers.Accept.ParseAdd(_accept); }
        catch { Request.Dispose(); throw; }
    }
    /// <summary>选中 Bearer/Host/Accept 覆盖同名默认值，其他默认认证和签名不能混入调用。</summary>
    internal void BeforeSend(HttpClient client)
    {
        foreach (var name in new[] { "api-key", "x-api-key", "x-goog-api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256" })
            if (client.DefaultRequestHeaders.Contains(name)) throw new InvalidOperationException("Perplexity 命名客户端含未选择的认证或签名。");
        Verify(null);
    }
    /// <summary>读取前后及拥有资源释放后都复查实际请求，不接受重定向或隐式目标替换。</summary>
    internal void Verify(HttpResponseMessage? response)
    {
        if (Request.Method != HttpMethod.Post || Request.RequestUri?.OriginalString != _endpoint || Request.Headers.Host != _host
            || response?.RequestMessage is { } actual && !ReferenceEquals(actual, Request)
            || Request.Headers.Authorization?.Scheme != "Bearer" || Request.Headers.Authorization.Parameter != _key
            || Request.Headers.Accept.Count != 1 || Request.Headers.Accept.Single().MediaType != _accept
            || !ReferenceEquals(Request.Content, _content) || _content.Headers.ContentType?.ToString() != "application/json; charset=utf-8") throw new InvalidDataException("Perplexity 实际目标、方法、认证或正文发生变化。");
    }
    /// <summary>即使外部处理器替换了正文，也释放最初冻结且归本次调用拥有的内容。</summary>
    public void Dispose() { try { Request.Dispose(); } finally { _content.Dispose(); } }
}
