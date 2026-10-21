using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Google.Internal;

namespace Asgard.Loki.LLM.Google;

/// <summary>Gemini 原生生成与 SSE，完整保留参数、工具和结果，不承担其他云认证。</summary>
public sealed partial class GoogleLlmProvider : IGeminiAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly GoogleHttpOptions _options;
    /// <summary>保留原构造签名，借用明确命名的 HTTP 工厂。</summary>
    public GoogleLlmProvider(IHttpClientFactory httpClientFactory, string id = "google", string httpClientName = "Asgard.Loki.LLM.Google")
        : this(httpClientFactory, new GoogleHttpOptions(), id, httpClientName) { }
    /// <summary>显式配置原生字节、尾部和候选资源策略，不猜测模型能力。</summary>
    public GoogleLlmProvider(IHttpClientFactory httpClientFactory, GoogleHttpOptions options, string id = "google", string httpClientName = "Asgard.Loki.LLM.Google")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBodyBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumTrackedCandidates, 0);
        GoogleConfiguredHttpEndpoint.Validate(options.Transport ?? new(), options.Authentication);
        _factory = httpClientFactory; _clientName = httpClientName; _options = options with { Transport = options.Transport is null ? null : options.Transport with { } }; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <summary>只有显式 None 接受 Anonymous；其他方式只接受 API Key，不借用外部账户。</summary>
    public bool SupportsCredential(LlmCredentialKind kind) => kind == (_options.Authentication == GoogleHttpAuthentication.None ? LlmCredentialKind.Anonymous : LlmCredentialKind.ApiKey);
    private bool ConfiguredTransport => _options.Transport is not null || _options.Authentication != GoogleHttpAuthentication.ApiKey;
    /// <summary>原生 HTTP 终态契约、命名客户端和所有资源策略参与完整 Session 绑定。</summary>
    public string BindingIdentity => GetType().AssemblyQualifiedName+":google-http-v2:"+JsonSerializer.Serialize(new { Client = _clientName, Options = _options });
    /// <inheritdoc />
    public async Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await SendAsync(request, context, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    public async IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await foreach (var item in StreamCoreAsync(request, context, cancellationToken).ConfigureAwait(false)) yield return item; }
    /// <summary>原生模型在路径，stream 由方法/alt 查询拥有，正文保持 Gemini 字段结构。</summary>
    private HttpRequestMessage Request(JsonObject payload, LlmAdapterContext context, bool stream, out GoogleConfiguredHttpCall? configured)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); GoogleHttpJson.Input(payload);
        if (!ConfiguredTransport && context.Credential.Kind != LlmCredentialKind.ApiKey) throw new ArgumentException("Gemini Developer API 需要明确 API Key。");
        var root = context.Target.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0 || root.OriginalString.Any(char.IsControl)) throw new ArgumentException("Gemini 需要无凭据、查询或片段的完整 HTTP(S) 根。");
        var model = context.Target.UpstreamModel; ArgumentException.ThrowIfNullOrWhiteSpace(model); GoogleHttpJson.Text(model); if (!ConfiguredTransport) GoogleHttpJson.Text(context.Credential.ApiKey);
        foreach (var property in payload) if ((property.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || property.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)) && property.Key is not ("model" or "stream")) throw new ArgumentException("Gemini model/stream 存在大小写别名冲突。");
        var body = payload.DeepClone().AsObject(); body.Remove("model"); body.Remove("stream"); var json = body.ToJsonString();
        if (GoogleHttpJson.Utf8.GetByteCount(json) > _options.MaximumRequestBytes) throw new InvalidDataException("Gemini 原生请求超过字节上限。");
        configured = null;
        if (ConfiguredTransport)
        {
            configured = new(GoogleHttpJson.Utf8.GetBytes(json), context, _options.Transport ?? new(), _options.Authentication, stream); return configured.Request;
        }
        var path = "models/"+Uri.EscapeDataString(model)+(stream ? ":streamGenerateContent?alt=sse" : ":generateContent");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/')+"/"), path)); request.Headers.Host = root.Authority;
        request.Headers.Add("x-goog-api-key", context.Credential.ApiKey); request.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json"));
        request.Content = new ByteArrayContent(GoogleHttpJson.Utf8.GetBytes(json)); request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; return request;
    }
    /// <summary>借用客户端不能附带另一种未选择的认证或签名，不改写工厂默认值。</summary>
    private static void ValidateDefaults(HttpClient client)
    { foreach (var header in new[] { "Authorization", "x-api-key", "api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256" }) if (client.DefaultRequestHeaders.Contains(header)) throw new InvalidOperationException("Gemini 命名客户端含未选择的认证或签名默认头。"); }
}
