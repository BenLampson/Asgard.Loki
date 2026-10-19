using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>Bedrock Claude/Converse 原生 JSON/EventStream，完整目标及显式 SigV4/Bearer 认证属于本供应商。</summary>
public sealed partial class AwsLlmProvider : IClaudeMessagesAdapter, IBedrockConverseAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly IAwsLlmCredentialSource? _source;
    private readonly bool _apiKey;
    private readonly string _sourceIdentity;
    private readonly AwsLlmOptions _options;
    private readonly string _clientName;
    private readonly TimeProvider _time;
    /// <summary>借用工厂、凭据来源和可选时钟；不自动寻找 AWS 配置或默认凭据。</summary>
    public AwsLlmProvider(IHttpClientFactory httpClientFactory, IAwsLlmCredentialSource credentialSource, AwsLlmOptions options,
        string id = "aws", string httpClientName = "Asgard.Loki.LLM.AWS", TimeProvider? timeProvider = null)
        : this(httpClientFactory, credentialSource, options, id, httpClientName, timeProvider, false) { }
    /// <summary>显式使用 Bedrock API Key/Bearer；不借用 SigV4 来源，不发现、签发或刷新 Key。</summary>
    public AwsLlmProvider(IHttpClientFactory httpClientFactory, AwsLlmOptions options,
        string id = "aws", string httpClientName = "Asgard.Loki.LLM.AWS")
        : this(httpClientFactory, null, options, id, httpClientName, null, true) { }
    /// <summary>两种认证入口共用原生传输配置，既有 SigV4 绑定字节保持不变。</summary>
    private AwsLlmProvider(IHttpClientFactory httpClientFactory, IAwsLlmCredentialSource? credentialSource, AwsLlmOptions options,
        string id, string httpClientName, TimeProvider? timeProvider, bool apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); if (!apiKey) ArgumentNullException.ThrowIfNull(credentialSource); ArgumentNullException.ThrowIfNull(options); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Region); if (options.Region.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')) throw new ArgumentException("AWS 区域必须是显式 ASCII 小写字母/数字/连字符。");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClaudeAnthropicVersion); AwsJson.Text(options.ClaudeAnthropicVersion); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBodyBytes, 0); ArgumentNullException.ThrowIfNull(options.RequestHeaders);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumEventStreamFrameBytes, 16); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumTrackedClaudeBlocks, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumEventStreamHeaders, 0);
        if (options.MaximumTrackedConverseBlocks is { } converseBlocks) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(converseBlocks, 0);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in options.RequestHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Key) || !header.Key.StartsWith("x-amzn-bedrock-", StringComparison.OrdinalIgnoreCase) || header.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) throw new ArgumentException("AWS 原生扩展头仅支持 x-amzn-bedrock-* 名称。");
            if (header.Value is null || header.Value.Any(character => character is < (char)32 or > (char)126)) throw new ArgumentException("AWS 原生头值必须是无控制字符的 ASCII 文本。"); headers.Add(header.Key.ToLowerInvariant(), header.Value);
        }
        _options = options with { RequestHeaders = new ReadOnlyDictionary<string, string>(headers) }; AwsJson.Text(id); AwsJson.Text(httpClientName); _sourceIdentity = apiKey ? "bedrock-api-key" : credentialSource!.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); AwsJson.Text(_sourceIdentity);
        _factory = httpClientFactory; _source = credentialSource; _apiKey = apiKey; _clientName = httpClientName; _time = timeProvider ?? TimeProvider.System; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + (_apiKey ? ":aws-bedrock-api-key-v1:" : ":aws-claude-eventstream-v1:") + JsonSerializer.Serialize(new { Options = _options, Client = _clientName, Source = _sourceIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == (_apiKey ? LlmCredentialKind.ApiKey : LlmCredentialKind.External);
    /// <summary>完整原生结果通过 EOF、长度、释放和最终取消检查后返回。</summary>
    public async Task<ClaudeMessage> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostJsonAsync(request, context, false, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <summary>完整 JSON 传输独立于协议正文；原生 API 由显式入口选择，不根据模型名猜测。</summary>
    private async Task<JsonElement> PostJsonAsync(JsonObject request, LlmAdapterContext context, bool converse, CancellationToken cancellationToken)
    {
        JsonElement result; var prepared = await PrepareAsync(request, context, false, cancellationToken, converse).ConfigureAwait(false);
        using (var message = prepared.Request)
        using (var client = _factory.CreateClient(_clientName))
        {
            AuthenticateBeforeSend(client, message, prepared.Body, prepared.Credentials, cancellationToken); using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await AwsJson.ReadAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _options.MaximumBodyBytes, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LlmUpstreamException("Bedrock 原生调用失败。", response.StatusCode, AwsJson.Decode(bytes));
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bedrock 完整调用没有返回 JSON。");
            result = AwsJson.Parse(bytes); if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bedrock 结果必须是原生对象。");
            if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null || !converse && result.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "error") throw new LlmUpstreamException("Bedrock 返回原生错误。", response.StatusCode, AwsJson.Decode(bytes));
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>首次来源等待前复制原生正文，只移除路由字段并选择 Bedrock 正文版本。</summary>
    private async Task<(HttpRequestMessage Request, byte[] Body, AwsLlmSigningCredentials? Credentials)> PrepareAsync(JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token, bool converse = false)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Bedrock 凭据种类与显式认证初始化入口不匹配。");
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.Query.Length != 0 || root.Fragment.Length != 0 || root.UserInfo.Length != 0) throw new ArgumentException("Bedrock Runtime 根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        var model = context.Target.UpstreamModel; ArgumentException.ThrowIfNullOrWhiteSpace(model); AwsJson.Text(model); if (model is "." or "..") throw new ArgumentException("Bedrock 模型 ID 不能是相对路径段。");
        AwsJson.Input(payload); var body = payload.DeepClone().AsObject(); body.Remove("model");
        if (!converse)
        {
            body.Remove("stream"); if (body.TryGetPropertyValue("anthropic_version", out var supplied) && (supplied is not JsonValue scalar || !scalar.TryGetValue<string>(out var version) || version != _options.ClaudeAnthropicVersion)) throw new ArgumentException("Claude 正文版本与所选 Bedrock 版本冲突。"); body["anthropic_version"] = _options.ClaudeAnthropicVersion;
        }
        if (stream && !converse && _options.RequestHeaders.TryGetValue("x-amzn-bedrock-accept", out var selected) && !JsonMedia(selected)) throw new ArgumentException("Claude EventStream 的模型内容类型必须选择 application/json。");
        var operation = converse ? stream ? "/converse-stream" : "/converse" : stream ? "/invoke-with-response-stream" : "/invoke";
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString()); var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), "model/" + Uri.EscapeDataString(model) + operation));
        try
        {
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json"); message.Headers.Accept.Add(new(stream ? "application/vnd.amazon.eventstream" : "application/json")); foreach (var header in _options.RequestHeaders) message.Headers.Add(header.Key, header.Value);
            if (stream && !converse && !message.Headers.Contains("x-amzn-bedrock-accept")) message.Headers.Add("x-amzn-bedrock-accept", "application/json");
            if (_apiKey) { PrepareApiKey(message, context.Credential.ApiKey); return (message, bytes, null); }
            SourceUnchanged(); var credentials = await _source!.GetCredentialsAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("AWS 来源没有返回签名凭据。"); token.ThrowIfCancellationRequested(); SourceUnchanged();
            ValidateCredentials(credentials, context.Credential.PrincipalIdentity); return (message, bytes, credentials);
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>发送前验证所选认证和默认头；SigV4 检查来源/期限并签名一次，Bearer 不访问签名来源。</summary>
    private void AuthenticateBeforeSend(HttpClient client, HttpRequestMessage message, byte[] body, AwsLlmSigningCredentials? credentials, CancellationToken token)
    {
        if (_apiKey) { ValidateApiKeyDefaults(client, message, token); return; }
        ArgumentNullException.ThrowIfNull(credentials);
        token.ThrowIfCancellationRequested(); SourceUnchanged(); var now = _time.GetUtcNow(); if (credentials.ExpiresOn <= now) throw new InvalidOperationException("AWS 凭据在发送前已过期。");
        foreach (var header in client.DefaultRequestHeaders)
        {
            var name = header.Key.ToLowerInvariant();
            if (name is "api-key" or "x-api-key" or "x-goog-api-key" || name.StartsWith("x-amzn-bedrock-", StringComparison.Ordinal) && !message.Headers.Contains(name) || name.StartsWith("x-amz-", StringComparison.Ordinal) && name is not ("x-amz-date" or "x-amz-content-sha256") && !(name == "x-amz-security-token" && credentials.SessionToken is not null)) throw new InvalidOperationException("Bedrock 默认头包含未选择的认证或原生请求配置。");
        }
        AwsSigV4.Sign(message, body, credentials, _options.Region, now); token.ThrowIfCancellationRequested();
    }
    /// <summary>稳定主体与来源的断言不能由正常短期 Key 轮换改变。</summary>
    private void ValidateCredentials(AwsLlmSigningCredentials credentials, string principal)
    {
        if (credentials.PrincipalIdentity != principal) throw new InvalidOperationException("AWS 来源返回不同主体。"); if (credentials.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("AWS 来源返回已过期凭据。");
    }
    /// <summary>配置漂移需要新供应商，不能在已有绑定下更换来源。</summary>
    private void SourceUnchanged()
    {
        if (_source is null || _source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("AWS 凭据来源配置已改变，应注册新供应商。");
    }
}
