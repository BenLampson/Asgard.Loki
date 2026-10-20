using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>单次原生文本请求快照和实际线协议校验；仅 method/model/stream 由已选调用拥有。</summary>
internal sealed class OpenAiTextCompletionCall
{
    private readonly string _key;
    private readonly string _endpoint;
    private readonly string _host;
    private readonly string _accept;
    private readonly HttpContent _content;
    internal HttpRequestMessage Request { get; }
    /// <summary>任何工厂等待之前冻结完整 UTF-8 请求，独立 Key 不借用其他认证或默认值。</summary>
    internal OpenAiTextCompletionCall(JsonObject payload, LlmAdapterContext context, bool stream, int maximum)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("文本补全需要无凭据、查询或片段的 HTTP(S) 完整根。");
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Target.UpstreamModel); OpenAiHttpJson.Text(context.Target.UpstreamModel);
        if (context.Credential.Kind != LlmCredentialKind.ApiKey) throw new NotSupportedException("兼容文本补全需要显式 API Key 凭据。");
        _key = context.Credential.ApiKey; if (_key.Any(c => c is < '!' or > '~')) throw new ArgumentException("原生 Key 必须为无空白/控制字符的 ASCII 文本。");
        ValidateOriginal(payload);
        foreach (var property in payload) if (new[] { "model", "stream" }.Any(x => x.Equals(property.Key, StringComparison.OrdinalIgnoreCase) && x != property.Key)) throw new ArgumentException("文本补全不能使用 model/stream 字段别名。");
        var body = payload.DeepClone().AsObject(); body["model"] = context.Target.UpstreamModel; body["stream"] = stream;
        var json = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true); if (utf8.GetByteCount(json) > maximum) throw new InvalidDataException("文本补全请求超过 UTF-8 字节上限。");
        _endpoint = root.AbsoluteUri.TrimEnd('/') + "/completions"; _host = root.Authority; _accept = stream ? "text/event-stream" : "application/json";
        Request = new(HttpMethod.Post, _endpoint); _content = new ByteArrayContent(utf8.GetBytes(json));
        try { Request.Content = _content; _content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; Request.Headers.Host = _host; Request.Headers.Authorization = new("Bearer", _key); Request.Headers.Accept.ParseAdd(_accept); }
        catch { Request.Dispose(); throw; }
    }
    /// <summary>原始 CLR 字符串和嵌入元素须先校验，不能被克隆/序列化替换或丢弃重复属性。</summary>
    private static void ValidateOriginal(JsonObject payload)
    {
        var pending = new Stack<JsonNode?>(); pending.Push(payload);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj) foreach (var property in obj) { OpenAiHttpJson.Text(property.Key); pending.Push(property.Value); }
            else if (node is JsonArray array) foreach (var child in array) pending.Push(child);
            else if (node is JsonValue value)
            {
                if (value.TryGetValue<JsonElement>(out var embedded)) _ = OpenAiHttpJson.Parse(Encoding.UTF8.GetBytes(embedded.GetRawText()));
                else if (value.TryGetValue<string>(out var text)) OpenAiHttpJson.Text(text);
                else if (value.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array) throw new ArgumentException("原生 JSON 容器必须显式使用 JsonObject/JsonArray，不能在 JsonValue 中隐藏 CLR 容器并改变原文。");
            }
        }
    }
    /// <summary>显式请求覆盖同名 Bearer/Host 默认值，但禁止工厂补入未选择的 Key 或签名。</summary>
    internal void BeforeSend(HttpClient client)
    {
        foreach (var name in new[] { "api-key", "x-api-key", "x-goog-api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256" })
            if (client.DefaultRequestHeaders.Contains(name)) throw new InvalidOperationException("文本补全命名客户端包含未选择的认证或签名。");
        Verify(null);
    }
    /// <summary>读取和所有拥有资源关闭后仍校验原始方法、完整根/模型请求及选中凭据；不跟随改写。</summary>
    internal void Verify(HttpResponseMessage? response)
    {
        if (Request.Method != HttpMethod.Post || Request.RequestUri?.OriginalString != _endpoint || Request.Headers.Host != _host
            || response?.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != _endpoint)
            || Request.Headers.Authorization?.Scheme != "Bearer" || Request.Headers.Authorization.Parameter != _key
            || Request.Headers.Accept.Count != 1 || Request.Headers.Accept.Single().MediaType != _accept
            || !ReferenceEquals(Request.Content, _content) || _content.Headers.ContentType?.ToString() != "application/json; charset=utf-8") throw new InvalidDataException("文本补全实际方法、目标、认证或正文改变。");
    }
}
