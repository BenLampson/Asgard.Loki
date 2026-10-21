using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.GoogleVertex.Internal;

namespace Asgard.Loki.LLM.GoogleVertex;

/// <summary>Vertex MaaS 原生 Chat JSON/SSE 调用；项目/区域端点和 publisher/model 均由完整目标明确指定。</summary>
public sealed partial class GoogleVertexMaaSLlmProvider : IChatCompletionsAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly IGoogleVertexLlmTokenSource _source;
    private readonly string _sourceIdentity;
    private readonly GoogleVertexMaaSLlmOptions _options;
    private readonly string _clientName;
    private readonly TimeProvider _time;
    private static readonly HttpRequestOptionsKey<DateTimeOffset> TokenExpiry = new("Asgard.Loki.GoogleVertex.MaaS.TokenExpiry");
    /// <summary>借用工厂、既有 Vertex 认证源和时钟；不登录、不刷新、不根据模型名选择端点。</summary>
    public GoogleVertexMaaSLlmProvider(IHttpClientFactory httpClientFactory, IGoogleVertexLlmTokenSource tokenSource,
        GoogleVertexMaaSLlmOptions? options = null, string id = "google-vertex-maas", string httpClientName = "Asgard.Loki.LLM.GoogleVertex.MaaS", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentNullException.ThrowIfNull(tokenSource); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _options = options is null ? new() : options with { }; ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumBodyBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumBufferedTerminalEvents, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumTrackedChoices, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaximumTrackedToolCalls, 0);
        VertexJson.Text(id); VertexJson.Text(httpClientName); _sourceIdentity = tokenSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); VertexJson.Text(_sourceIdentity);
        if (_options.QuotaProjectId is { } quota) { ArgumentException.ThrowIfNullOrWhiteSpace(quota); VertexJson.Text(quota); if (quota.Any(char.IsWhiteSpace) || quota.Any(char.IsControl)) throw new ArgumentException("Vertex MaaS quota project 不能包含空白或控制字符。", nameof(options)); }
        _factory = httpClientFactory; _source = tokenSource; _clientName = httpClientName; _time = timeProvider ?? TimeProvider.System; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":vertex-maas-sse-v1:" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName, Source = _sourceIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.External;
    /// <summary>原生 Chat 对象在完整 EOF、长度、释放和最终取消检查后返回；不映射内容或重试。</summary>
    public async Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        JsonElement result;
        using (var message = await PrepareAsync(request, context, false, cancellationToken).ConfigureAwait(false))
        using (var client = _factory.CreateClient(_clientName))
        {
            BeforeSend(client, message, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await VertexJson.ReadAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("Vertex MaaS 原生调用失败。", response.StatusCode, VertexJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Vertex MaaS 完整调用没有返回 JSON。");
            result = VertexJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Vertex MaaS 结果必须是原生对象。");
            if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new LlmUpstreamException("Vertex MaaS 返回原生错误。", response.StatusCode, VertexJson.Decode(bytes));
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(result, context.Metadata);
    }
    /// <summary>第一次认证等待前快照请求；只替换已绑定的实际模型和本次调用传输标志。</summary>
    private async Task<HttpRequestMessage> PrepareAsync(JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Vertex MaaS 要求显式 External 主体凭据。");
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.Query.Length != 0 || root.Fragment.Length != 0 || root.UserInfo.Length != 0) throw new ArgumentException("Vertex MaaS 根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        var segments = root.AbsolutePath.TrimEnd('/').Split('/');
        if (segments.Length < 7 || segments[^6] != "projects" || segments[^4] != "locations" || segments[^2] != "endpoints" || segments[^1] != "openapi" || string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segments[^5])) || string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segments[^3]))) throw new ArgumentException("Vertex MaaS 根地址必须显式以 projects/{project}/locations/{location}/endpoints/openapi 结尾。");
        var model = context.Target.UpstreamModel; ArgumentException.ThrowIfNullOrWhiteSpace(model); VertexJson.Text(model); var parts = model.Split('/');
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")) throw new ArgumentException("Vertex MaaS 实际模型必须显式为 publisher/model。");
        VertexJson.Input(payload); var body = payload.DeepClone().AsObject(); body["model"] = model; body["stream"] = stream;
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), "chat/completions"));
        try
        {
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); message.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json")); if (_options.QuotaProjectId is { } quota) message.Headers.Add("x-goog-user-project", quota);
            SourceUnchanged(); var access = await _source.GetTokenAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Vertex MaaS 认证源未返回令牌。"); token.ThrowIfCancellationRequested(); SourceUnchanged();
            if (access.PrincipalIdentity != context.Credential.PrincipalIdentity) throw new InvalidOperationException("Vertex MaaS 认证源返回不同主体。");
            if (access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex MaaS 认证源返回已过期令牌。");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken); message.Options.Set(TokenExpiry, access.ExpiresOn); return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>发送前复查来源和令牌期限；同类选中头覆盖默认头，不继承其他认证。</summary>
    private void BeforeSend(HttpClient client, HttpRequestMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); SourceUnchanged();
        if (client.DefaultRequestHeaders.Contains("x-goog-api-key") || client.DefaultRequestHeaders.Contains("api-key") || client.DefaultRequestHeaders.Contains("x-api-key") || _options.QuotaProjectId is null && client.DefaultRequestHeaders.Contains("x-goog-user-project")) throw new InvalidOperationException("Vertex MaaS 默认头含未选择的认证或项目身份。");
        if (!message.Options.TryGetValue(TokenExpiry, out var expiry) || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex MaaS 令牌在发送前已过期。");
    }
    /// <summary>来源配置不能在同一供应商实现身份上漂移。</summary>
    private void SourceUnchanged()
    {
        if (_source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Vertex MaaS 认证源配置已改变，应注册新的供应商。");
    }
}
