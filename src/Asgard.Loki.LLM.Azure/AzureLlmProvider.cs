using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Azure.Internal;

namespace Asgard.Loki.LLM.Azure;

/// <summary>Azure 原生路由和认证独立属于本供应商，不引用其他供应商或管理云资源。</summary>
public sealed partial class AzureLlmProvider : IChatCompletionsAdapter, IResponsesAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly AzureLlmOptions _options;
    private readonly IAzureLlmTokenSource? _tokenSource;
    private readonly string? _sourceIdentity;
    private readonly TimeProvider _time;
    private static readonly HttpRequestOptionsKey<DateTimeOffset> TokenExpiry = new("Asgard.Loki.Azure.TokenExpiry");
    /// <summary>借用 HTTP 工厂、可选认证源和时钟；捕获版本与稳定认证配置。</summary>
    public AzureLlmProvider(IHttpClientFactory factory, AzureLlmOptions? options = null, IAzureLlmTokenSource? tokenSource = null,
        string id = "azure", string httpClientName = "Asgard.Loki.LLM.Azure", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        options = options is null ? new() : options with { };
        if (!Enum.IsDefined(options.ApiStyle)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.ApiStyle == AzureLlmApiStyle.Dated || options.ApiVersion is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBodyBytes, 0);
        AzureJson.Text(id); AzureJson.Text(httpClientName); if (options.ApiVersion is not null) AzureJson.Text(options.ApiVersion);
        _factory = factory; _clientName = httpClientName; _options = options; _tokenSource = tokenSource; _time = timeProvider ?? TimeProvider.System; Id = id;
        if (tokenSource is not null) { var sourceIdentity = tokenSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity); AzureJson.Text(sourceIdentity); _sourceIdentity = sourceIdentity; }
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":azure-json-v1:" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName, Source = _sourceIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.ApiKey || kind == LlmCredentialKind.External && _tokenSource is not null;
    /// <inheritdoc />
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync(request, context, false, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync(request, context, true, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <summary>先提供完整 JSON，SSE 作为独立原生终态检查点补齐。</summary>
    public IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("此检查点尚未实现 Azure 原生 SSE。");
    /// <summary>明确区分 Responses 流类型，不用完整 JSON 伪装 SSE。</summary>
    IAsyncEnumerable<ResponseEvent> IResponsesAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("此检查点尚未实现 Azure 原生 SSE。");
    /// <summary>结果在 EOF 和释放成功后返回，不自动重试生成或再次获取令牌。</summary>
    private async Task<JsonElement> PostAsync(JsonObject payload, LlmAdapterContext context, bool responses, CancellationToken token)
    {
        JsonElement result;
        using (var request = await PrepareAsync(payload, context, responses, false, token).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, request, context, token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var bytes = await AzureJson.ReadAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), _options.MaximumBodyBytes, response.Content.Headers.ContentLength, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("Azure 原生调用失败。", response.StatusCode, AzureJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Azure 完整调用没有返回 JSON。");
            result = AzureJson.Parse(bytes);
            if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Azure 结果必须是原生 JSON 对象。");
            if (AzureJson.IsError(result)) throw new LlmUpstreamException("Azure 返回原生错误。", response.StatusCode, AzureJson.Decode(bytes));
        }
        token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>认证源首次等待前复制报文，只修改实际部署和显式传输选择。</summary>
    private async Task<HttpRequestMessage> PrepareAsync(JsonObject payload, LlmAdapterContext context, bool responses, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Azure 未声明支持所选认证方式。");
        var target = context.Target; ArgumentNullException.ThrowIfNull(target); var root = target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("Azure 根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel); AzureJson.Text(target.UpstreamModel); AzureJson.Input(payload);
        var body = payload.DeepClone().AsObject(); body["model"] = target.UpstreamModel; body["stream"] = stream;
        var path = responses ? "responses" : "chat/completions";
        if (_options.ApiStyle == AzureLlmApiStyle.Dated && !responses)
        {
            if (target.UpstreamModel is "." or "..") throw new ArgumentException("Azure 部署名不能是相对路径段。");
            path = "deployments/" + Uri.EscapeDataString(target.UpstreamModel) + "/chat/completions"; body.Remove("model");
        }
        if (_options.ApiVersion is not null) path += "?api-version=" + Uri.EscapeDataString(_options.ApiVersion);
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path));
        try
        {
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); message.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json"));
            if (context.Credential.Kind == LlmCredentialKind.ApiKey) message.Headers.Add("api-key", context.Credential.ApiKey);
            else
            {
                SourceUnchanged();
                var access = await _tokenSource!.GetTokenAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Azure 认证源未返回令牌。");
                token.ThrowIfCancellationRequested(); SourceUnchanged();
                if (access.PrincipalIdentity != context.Credential.PrincipalIdentity) throw new InvalidOperationException("Azure 认证源返回不同主体，不允许切换身份。");
                if (access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Azure 认证源返回已过期令牌。");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken); message.Options.Set(TokenExpiry, access.ExpiresOn);
            }
            token.ThrowIfCancellationRequested(); return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>不继承第二种未选认证头；本次同类头覆盖默认值，不修改借用工厂。</summary>
    private void BeforeSend(HttpClient client, HttpRequestMessage request, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.Credential.Kind == LlmCredentialKind.ApiKey && client.DefaultRequestHeaders.Authorization is not null
            || context.Credential.Kind == LlmCredentialKind.External && client.DefaultRequestHeaders.Contains("api-key")) throw new InvalidOperationException("Azure 工厂默认头含另一种未选认证身份。");
        if (context.Credential.Kind == LlmCredentialKind.External)
        {
            SourceUnchanged(); if (!request.Options.TryGetValue(TokenExpiry, out var expiry) || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Azure 令牌在发送前已过期。");
        }
    }
    /// <summary>借用认证配置不可在原实例上变化，应用应注册新的供应商实例。</summary>
    private void SourceUnchanged()
    {
        if (_tokenSource is null || _tokenSource.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Azure 认证源配置已变化，不能沿用原实现身份。");
    }
}
