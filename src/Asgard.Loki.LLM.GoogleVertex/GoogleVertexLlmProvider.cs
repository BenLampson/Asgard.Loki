using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.GoogleVertex.Internal;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Vertex Gemini 原生资源路径和借用式认证，不引用 Developer API 或其他供应商包。</summary>
public sealed class GoogleVertexLlmProvider : IGeminiAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly IGoogleVertexLlmTokenSource _source;
    private readonly string _sourceIdentity;
    private readonly string _clientName;
    private readonly GoogleVertexLlmOptions _options;
    private readonly TimeProvider _time;
    private static readonly HttpRequestOptionsKey<DateTimeOffset> TokenExpiry = new("Asgard.Loki.GoogleVertex.TokenExpiry");
    /// <summary>借用工厂、认证源和可选时钟；版本路径与区域主机显式放在目标基础地址。</summary>
    public GoogleVertexLlmProvider(IHttpClientFactory httpClientFactory, IGoogleVertexLlmTokenSource tokenSource, GoogleVertexLlmOptions? options = null,
        string id = "google-vertex", string httpClientName = "Asgard.Loki.LLM.GoogleVertex", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentNullException.ThrowIfNull(tokenSource); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _options = options is null ? new() : options with { }; ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumBodyBytes, 0);
        VertexJson.Text(id); VertexJson.Text(httpClientName); _sourceIdentity = tokenSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); VertexJson.Text(_sourceIdentity);
        if (_options.QuotaProjectId is { } quota) { ArgumentException.ThrowIfNullOrWhiteSpace(quota); VertexJson.Text(quota); if (quota.Any(char.IsWhiteSpace) || quota.Any(char.IsControl)) throw new ArgumentException("Vertex quota project 不能包含空白或控制字符。", nameof(options)); }
        _factory = httpClientFactory; _source = tokenSource; _clientName = httpClientName; _time = timeProvider ?? TimeProvider.System; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":vertex-json-v1:" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName, Source = _sourceIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.External;
    /// <summary>完整结果通过原始 EOF、长度、释放和最终取消检查后返回。</summary>
    public async Task<GeminiContent> GenerateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        JsonElement result;
        using (var message = await PrepareAsync(request, context, false, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, message, cancellationToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await VertexJson.ReadAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("Vertex 原生调用失败。", response.StatusCode, VertexJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Vertex 完整调用没有返回 JSON。");
            result = VertexJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex 结果必须是原生 JSON 对象。");
            if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new LlmUpstreamException("Vertex 返回原生错误。", response.StatusCode, VertexJson.Decode(bytes));
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(result, context.Metadata);
    }
    /// <summary>流式作为下一独立检查点，不用缓冲完整 JSON 伪装流式事件。</summary>
    public IAsyncEnumerable<GeminiContentEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("此检查点尚未实现 Vertex 原生流式调用。");
    /// <summary>认证源等待前快照，仅去掉 SDK 路由/传输字段，保留所有原生内容与关联。</summary>
    private async Task<HttpRequestMessage> PrepareAsync(JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Vertex 标准调用要求显式 External 主体凭据。");
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.Query.Length != 0 || root.Fragment.Length != 0 || root.UserInfo.Length != 0) throw new ArgumentException("Vertex API 根必须是无凭据、查询或片段的 HTTP(S) 绝对地址。");
        var resource = ResourcePath(context.Target.UpstreamModel); VertexJson.Input(payload); var body = payload.DeepClone().AsObject(); body.Remove("model"); body.Remove("stream");
        var path = resource + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent");
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path));
        try
        {
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); message.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json"));
            if (_options.QuotaProjectId is { } quota) message.Headers.Add("x-goog-user-project", quota);
            SourceUnchanged(); var access = await _source.GetTokenAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Vertex 认证源未返回令牌。");
            token.ThrowIfCancellationRequested(); SourceUnchanged();
            if (access.PrincipalIdentity != context.Credential.PrincipalIdentity) throw new InvalidOperationException("Vertex 认证源返回不同主体，不允许切换身份。");
            if (access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex 认证源返回已过期令牌。");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken); message.Options.Set(TokenExpiry, access.ExpiresOn); return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>完整资源名显式表达项目/区域和 publisher 模型或 tuned endpoint，不猜测模型别名。</summary>
    private static string ResourcePath(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model); VertexJson.Text(model); var parts = model.Split('/');
        if (!(parts.Length == 8 && parts[0] == "projects" && parts[2] == "locations" && parts[4] == "publishers" && parts[6] == "models"
            || parts.Length == 6 && parts[0] == "projects" && parts[2] == "locations" && parts[4] == "endpoints")
            || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")) throw new ArgumentException("Vertex 模型必须是完整 publisher 模型或 tuned endpoint 资源名。", nameof(model));
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }
    /// <summary>默认头不能引入未绑定的 API Key 或 quota project，同类 Bearer 由本次请求覆盖。</summary>
    private void BeforeSend(HttpClient client, HttpRequestMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); SourceUnchanged();
        if (client.DefaultRequestHeaders.Contains("x-goog-api-key") || client.DefaultRequestHeaders.Contains("api-key") || _options.QuotaProjectId is null && client.DefaultRequestHeaders.Contains("x-goog-user-project")) throw new InvalidOperationException("Vertex HTTP 默认头包含未选择的认证或项目身份。");
        if (!message.Options.TryGetValue(TokenExpiry, out var expiry) || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex 令牌在发送前已过期。");
    }
    /// <summary>来源配置不能在同一实现身份上漂移。</summary>
    private void SourceUnchanged()
    {
        if (_source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Vertex 认证源配置已改变，应注册新的供应商。");
    }
}
