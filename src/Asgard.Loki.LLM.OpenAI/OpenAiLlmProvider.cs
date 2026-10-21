using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.OpenAI.Internal;
using System.Collections.ObjectModel;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>实现 OpenAI Chat/Responses HTTP/JSON、SSE、原生 Moderations 及可选双工协议。</summary>
public sealed partial class OpenAiLlmProvider : IChatCompletionsAdapter, IResponsesAdapter, IResponsesCompactionAdapter, IResponsesWebSocketAdapter, IRealtimeAdapter, IModerationsAdapter, ITextCompletionsAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;
    private readonly IOpenAiResponsesWebSocketFactory _webSocketFactory;
    private readonly OpenAiResponsesWebSocketOptions _webSocketOptions;
    private readonly OpenAiHttpOptions _httpOptions;
    private readonly IOpenAiRealtimeWebSocketFactory _realtimeFactory;
    private readonly OpenAiRealtimeOptions _realtimeOptions;

    /// <summary>借用命名 HTTP 工厂；调用者应关闭生成类 POST 请求的自动重试。</summary>
    public OpenAiLlmProvider(IHttpClientFactory httpClientFactory, string id = "openai", string httpClientName = "Asgard.Loki.LLM.OpenAI")
        : this(httpClientFactory, id, httpClientName, null, null, null) { }

    /// <summary>显式配置原生双工资源策略；借用可选连接工厂，保留原 HTTP 构造函数签名。</summary>
    public OpenAiLlmProvider(IHttpClientFactory httpClientFactory, OpenAiResponsesWebSocketOptions webSocketOptions,
        IOpenAiResponsesWebSocketFactory? webSocketFactory = null, string id = "openai", string httpClientName = "Asgard.Loki.LLM.OpenAI")
        : this(httpClientFactory, id, httpClientName, webSocketFactory, webSocketOptions, null)
        => ArgumentNullException.ThrowIfNull(webSocketOptions);

    /// <summary>显式配置 HTTP 字节/终态尾部策略，原有 HTTP 构造签名仍保留。</summary>
    public OpenAiLlmProvider(IHttpClientFactory httpClientFactory, OpenAiHttpOptions httpOptions,
        string id = "openai", string httpClientName = "Asgard.Loki.LLM.OpenAI")
        : this(httpClientFactory, id, httpClientName, null, null, httpOptions)
        => ArgumentNullException.ThrowIfNull(httpOptions);

    /// <summary>分别配置 HTTP 和 WebSocket 的本地策略，不共享两种传输的业务状态。</summary>
    public OpenAiLlmProvider(IHttpClientFactory httpClientFactory, OpenAiHttpOptions httpOptions, OpenAiResponsesWebSocketOptions webSocketOptions,
        IOpenAiResponsesWebSocketFactory? webSocketFactory = null, string id = "openai", string httpClientName = "Asgard.Loki.LLM.OpenAI")
        : this(httpClientFactory, id, httpClientName, webSocketFactory, webSocketOptions, httpOptions)
    { ArgumentNullException.ThrowIfNull(httpOptions); ArgumentNullException.ThrowIfNull(webSocketOptions); }

    /// <summary>在任何网络调用前保存不可变策略快照。</summary>
    private OpenAiLlmProvider(IHttpClientFactory httpClientFactory, string id, string httpClientName,
        IOpenAiResponsesWebSocketFactory? webSocketFactory, OpenAiResponsesWebSocketOptions? webSocketOptions, OpenAiHttpOptions? httpOptions,
        OpenAiRealtimeOptions? realtimeOptions = null, IOpenAiRealtimeWebSocketFactory? realtimeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
        _webSocketFactory = webSocketFactory ?? new DefaultResponsesWebSocketFactory();
        _webSocketOptions = webSocketOptions is null ? new() : webSocketOptions with { };
        _httpOptions = httpOptions is null ? new() : httpOptions with { Transport = httpOptions.Transport is null ? null : httpOptions.Transport with { } };
        if (_httpOptions.Transport is not null) OpenAiConfiguredHttpEndpoint.Validate(_httpOptions.Transport);
        _realtimeOptions = realtimeOptions is null ? new() : realtimeOptions with { };
        _realtimeFactory = realtimeFactory ?? new DefaultResponsesWebSocketFactory();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_realtimeOptions.MaximumMessageBytes, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(_realtimeFactory.BindingIdentity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_httpOptions.MaximumBodyBytes, 0);
        if (_httpOptions.MaximumModerationRequestBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(httpOptions));
        if (_httpOptions.MaximumTextCompletionRequestBytes is <= 0 || _httpOptions.MaximumTrackedTextChoices is <= 0) throw new ArgumentOutOfRangeException(nameof(httpOptions));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_httpOptions.MaximumBufferedTerminalEvents, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(_webSocketFactory.BindingIdentity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_webSocketOptions.MaximumMessageBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_webSocketOptions.MaximumPendingRequests, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_webSocketOptions.MaximumTrackedResponses, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_webSocketOptions.MaximumTrackedEventIds, 0);
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>显式无认证只接受 Anonymous；默认及其他 HTTP 方式只接受 API Key，不接受外部账户。</summary>
    public bool SupportsCredential(LlmCredentialKind kind) => kind == (_httpOptions.Transport?.Authentication == OpenAiHttpAuthentication.None ? LlmCredentialKind.Anonymous : LlmCredentialKind.ApiKey);

    /// <summary>HTTP 终态/资源语义使用新域；HTTP、WebSocket 策略和自定义连接工厂均参与完整绑定。</summary>
    public string BindingIdentity => _realtimeFactory is DefaultResponsesWebSocketFactory && _realtimeOptions == new OpenAiRealtimeOptions()
        ? ResponsesBindingIdentity
        : ResponsesBindingIdentity + "\nrealtime-ws-v1\n" + _realtimeFactory.BindingIdentity + "\n" + JsonSerializer.Serialize(_realtimeOptions);

    /// <summary>默认新增 Realtime 策略不改变已有 HTTP/Responses 会话的绑定字节。</summary>
    private string ResponsesBindingIdentity => _webSocketFactory is DefaultResponsesWebSocketFactory && _webSocketOptions == new OpenAiResponsesWebSocketOptions()
        ? HttpBindingIdentity
        : HttpBindingIdentity + "\nresponses-ws-v1\n" + _webSocketFactory.BindingIdentity + "\n" + JsonSerializer.Serialize(_webSocketOptions);

    /// <summary>身份不包含 Key；旧不检查 EOF 的 HTTP 会话不能沿用新终态契约。</summary>
    private string HttpBindingIdentity => GetType().AssemblyQualifiedName + "\nopenai-http-v2\n" + JsonSerializer.Serialize(new { Options = _httpOptions, Client = _httpClientName });

    /// <inheritdoc />
    public async Task<IResponsesWebSocketConnection> OpenWebSocketAsync(LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (_httpOptions.Transport?.Authentication == OpenAiHttpAuthentication.None && context.Credential.Kind != LlmCredentialKind.ApiKey) throw new NotSupportedException("原生 WebSocket 需要独立的 API Key 认证，不继承 HTTP 无认证配置。");
        var baseUri = context.Target.BaseAddress;
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https") || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 || baseUri.UserInfo.Length != 0)
            throw new ArgumentException("WebSocket 目标必须使用没有凭据、查询和片段的 HTTP(S) 基础地址。", nameof(context));
        var endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/responses");
        var address = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws", Port = endpoint.IsDefaultPort ? -1 : endpoint.Port }.Uri;
        var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Authorization"] = "Bearer " + context.Credential.ApiKey });
        var socket = await _webSocketFactory.ConnectAsync(address, headers, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("连接工厂未返回 WebSocket。");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new OpenAiResponsesWebSocketConnection(new(socket, _webSocketOptions.MaximumMessageBytes), context.Metadata, _webSocketOptions);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync("chat/completions", request, context, cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync("responses", request, context, cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async Task<ResponseCompaction> CompactAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync("responses/compact", request, context, cancellationToken).ConfigureAwait(false), context.Metadata);

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamProtocolAsync("chat/completions", request, context, false, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(item.Data);
            yield return new ChatCompletionEvent(document.RootElement, context.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
        }
    }

    /// <summary>显式区分两种协议的流式结果类型。</summary>
    async IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in StreamProtocolAsync("responses", request, context, true, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(item.Data);
            yield return new ResponseEvent(document.RootElement, context.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
        }
    }

    /// <summary>地址、认证和正文均从已绑定的目标构造，不修改 HTTP 工厂默认头。</summary>
    private static HttpRequestMessage Request(string path, JsonObject payload, LlmAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        OpenAiHttpJson.Input(payload);
        var root = context.Target.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("OpenAI HTTP 根地址必须是没有凭据、查询或片段的 HTTP(S) 地址。");
        var baseAddress = context.Target.BaseAddress.AbsoluteUri.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseAddress), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.ApiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

}
