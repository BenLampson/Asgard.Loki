using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Anthropic.Internal;

namespace Asgard.Loki.LLM.Anthropic;

/// <summary>实现 Claude Messages 原生 HTTP/JSON 与 SSE，不改写消息或推理内容。</summary>
public sealed partial class AnthropicLlmProvider : IClaudeMessagesAdapter
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
        _factory = httpClientFactory; _clientName = httpClientName; _apiVersion = apiVersion; _options = options with { }; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
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
    /// <summary>在明确基础路径后拼接 messages，正文和认证只属于本次请求。</summary>
    private HttpRequestMessage Request(JsonObject payload, LlmAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); AnthropicHttpJson.Input(payload);
        var root = context.Target.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Claude 根地址必须是没有凭据、查询或片段的 HTTP(S) 地址。", nameof(context));
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
