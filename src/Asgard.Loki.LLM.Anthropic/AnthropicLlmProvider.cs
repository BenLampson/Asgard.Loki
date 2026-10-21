using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Anthropic.Internal;

namespace Asgard.Loki.LLM.Anthropic;

/// <summary>实现 Claude Messages 原生 HTTP/JSON 与 SSE，不改写消息或推理内容。</summary>
public sealed partial class AnthropicLlmProvider : IClaudeMessagesAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly string _apiVersion;
    private readonly AnthropicHttpOptions _options;

    /// <summary>保留原构造签名；默认使用 x-api-key，借用 HTTP 工厂。</summary>
    public AnthropicLlmProvider(IHttpClientFactory httpClientFactory, string id = "anthropic",
        string httpClientName = "Asgard.Loki.LLM.Anthropic", string apiVersion = "2023-06-01")
        : this(httpClientFactory, new AnthropicHttpOptions(), id, httpClientName, apiVersion) { }

    /// <summary>明确选择认证头及本地资源策略，不按地址或模型推断云协议。</summary>
    public AnthropicLlmProvider(IHttpClientFactory httpClientFactory, AnthropicHttpOptions options, string id = "anthropic",
        string httpClientName = "Asgard.Loki.LLM.Anthropic", string apiVersion = "2023-06-01")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName); ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        if (!Enum.IsDefined(options.Authentication)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBodyBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumTrackedContentBlocks, 0);
        AnthropicHttpJson.Text(apiVersion); if (apiVersion.Any(char.IsControl)) throw new ArgumentException("API 版本不能包含控制字符。", nameof(apiVersion));
        AnthropicConfiguredHttpEndpoint.Validate(options.Transport ?? new(), options.Authentication);
        _factory = httpClientFactory; _clientName = httpClientName; _apiVersion = apiVersion; _options = options with { Transport = options.Transport is null ? null : options.Transport with { } }; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <summary>无认证必须明确使用 Anonymous；其他模式只接受 API Key，不借用外部账户。</summary>
    public bool SupportsCredential(LlmCredentialKind kind) => kind == (_options.Authentication == AnthropicAuthentication.None ? LlmCredentialKind.Anonymous : LlmCredentialKind.ApiKey);
    private bool ConfiguredTransport => _options.Transport is not null || _options.Authentication is not (AnthropicAuthentication.ApiKey or AnthropicAuthentication.Bearer);
    /// <summary>新 HTTP 终态契约、认证模式、版本、命名客户端和资源策略参与完整绑定，不包含 Key。</summary>
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":anthropic-http-v2:" + JsonSerializer.Serialize(new { ApiVersion = _apiVersion, Client = _clientName, Options = _options });
    /// <inheritdoc />
    public async Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await SendAsync(request, context, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    public async IAsyncEnumerable<ClaudeMessageEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in StreamFramesAsync(request, context, cancellationToken).ConfigureAwait(false))
            yield return new ClaudeMessageEvent(AnthropicHttpJson.Parse(Encoding.UTF8.GetBytes(frame.Data)), context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
    }
    /// <summary>从完整目标构造原生操作与唯一认证；默认路径仍为 messages，正文只属于本次请求。</summary>
    private HttpRequestMessage Request(JsonObject payload, LlmAdapterContext context, bool stream, out AnthropicConfiguredHttpCall? configured)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); AnthropicHttpJson.Input(payload);
        var root = context.Target.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Claude 根地址必须是没有凭据、查询或片段的 HTTP(S) 地址。", nameof(context));
        configured = null;
        if (ConfiguredTransport)
        {
            configured = new(Encoding.UTF8.GetBytes(payload.ToJsonString()), context, _options.Transport ?? new(), _options.Authentication, _apiVersion, stream);
            return configured.Request;
        }
        var key = context.Credential.ApiKey;
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root.AbsoluteUri.TrimEnd('/') + "/messages"));
        request.Headers.Host = root.Authority;
        if (_options.Authentication == AnthropicAuthentication.Bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", _apiVersion);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"); return request;
    }
    /// <summary>不把借用客户端的另一种认证或签名带到本次目标，也不修改工厂默认配置。</summary>
    private void ValidateDefaults(HttpClient client)
    {
        var unselected = _options.Authentication == AnthropicAuthentication.Bearer ? "x-api-key" : "Authorization";
        foreach (var name in new[] { unselected, "x-goog-api-key", "api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256" })
            if (client.DefaultRequestHeaders.Contains(name)) throw new InvalidOperationException("Claude 命名客户端包含未选择的认证或签名默认头。");
    }
}
