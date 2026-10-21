using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.AWS.Internal;

namespace Asgard.Loki.LLM.AWS;

/// <summary>显式 Mantle SigV4 Messages JSON/SSE，不复用 InvokeModel 封装或发现 AWS 身份。</summary>
public sealed partial class AwsMantleLlmProvider : IClaudeMessagesAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly IAwsLlmCredentialSource _source;
    private readonly string _sourceIdentity;
    private readonly AwsMantleLlmOptions _options;
    private readonly string _clientName;
    private readonly TimeProvider _time;
    /// <summary>借用完整目标之外的签名配置、来源、工厂和时钟；来源负责应用侧登录/刷新。</summary>
    public AwsMantleLlmProvider(IHttpClientFactory httpClientFactory, IAwsLlmCredentialSource credentialSource, AwsMantleLlmOptions options,
        string id = "aws-mantle", string httpClientName = "Asgard.Loki.LLM.AWS.Mantle", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory); ArgumentNullException.ThrowIfNull(credentialSource); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName); ArgumentException.ThrowIfNullOrWhiteSpace(options.Region);
        if (options.Region.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')) throw new ArgumentException("Mantle 区域必须是明确的 ASCII 小写字母/数字/连字符。");
        CheckValue(options.AnthropicVersion); ArgumentException.ThrowIfNullOrWhiteSpace(options.AnthropicVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBodyBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumTrackedContentBlocks, 0);
        ArgumentNullException.ThrowIfNull(options.RequestHeaders); var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in options.RequestHeaders)
        {
            var name = entry.Key?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name) || name == "anthropic-version" || !(name.StartsWith("anthropic-", StringComparison.Ordinal) || name.StartsWith("x-amzn-bedrock-", StringComparison.Ordinal)) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Mantle 仅接受明确的原生扩展头，版本使用独立选项。");
            CheckValue(entry.Value); headers.Add(name, entry.Value);
        }
        _sourceIdentity = credentialSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); AwsJson.Text(_sourceIdentity); AwsJson.Text(id); AwsJson.Text(httpClientName);
        _options = options with { RequestHeaders = new ReadOnlyDictionary<string, string>(headers) }; _factory = httpClientFactory; _source = credentialSource; _clientName = httpClientName; _time = timeProvider ?? TimeProvider.System; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <summary>服务、稳定来源、版本、头和资源策略参与绑定，不包含短期签名材料。</summary>
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":aws-mantle-messages-sigv4-v1:" + JsonSerializer.Serialize(new { Service = "bedrock-mantle", Options = _options, Client = _clientName, Source = _sourceIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.External;
    /// <summary>源等待前准备完整原生正文；仅固定模型和本次调用的流模式。</summary>
    private async Task<(HttpRequestMessage Message, byte[] Body, AwsLlmSigningCredentials Credentials)> PrepareAsync(JsonObject payload, LlmAdapterContext context, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Mantle SigV4 必须明确使用 External 主体身份。");
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.Query.Length != 0 || root.Fragment.Length != 0 || root.UserInfo.Length != 0) throw new ArgumentException("Mantle 根地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Target.UpstreamModel); AwsJson.Text(context.Target.UpstreamModel); AwsJson.Input(payload);
        var body = payload.DeepClone().AsObject(); body["model"] = context.Target.UpstreamModel; body["stream"] = stream; var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(root.AbsoluteUri.TrimEnd('/') + "/messages"));
        try
        {
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json"); message.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json")); message.Headers.Add("anthropic-version", _options.AnthropicVersion);
            foreach (var header in _options.RequestHeaders) message.Headers.Add(header.Key, header.Value);
            SourceUnchanged(); var credentials = await _source.GetCredentialsAsync(context.Credential.PrincipalIdentity, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Mantle 来源没有返回签名凭据。");
            token.ThrowIfCancellationRequested(); SourceUnchanged(); if (credentials.PrincipalIdentity != context.Credential.PrincipalIdentity) throw new InvalidOperationException("Mantle 来源返回不同主体。");
            if (credentials.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Mantle 来源返回已过期凭据。"); return (message, bytes, credentials);
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>工厂回调后再次验证期限与来源，并签名最终实际正文和原生头；不重签重试。</summary>
    private void SignBeforeSend(HttpClient client, HttpRequestMessage message, byte[] body, AwsLlmSigningCredentials credentials, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); SourceUnchanged(); var now = _time.GetUtcNow(); if (credentials.ExpiresOn <= now) throw new InvalidOperationException("Mantle 凭据在发送前已过期。");
        foreach (var header in client.DefaultRequestHeaders)
        {
            var name = header.Key.ToLowerInvariant();
            if (name is "x-api-key" or "api-key" or "x-goog-api-key"
                || (name.StartsWith("anthropic-", StringComparison.Ordinal) || name.StartsWith("x-amzn-bedrock-", StringComparison.Ordinal)) && !message.Headers.Contains(name)
                || name.StartsWith("x-amz-", StringComparison.Ordinal) && name is not ("x-amz-date" or "x-amz-content-sha256") && !(name == "x-amz-security-token" && credentials.SessionToken is not null))
                throw new InvalidOperationException("Mantle 默认头包含未选择的认证、签名或原生配置。");
        }
        AwsSigV4.Sign(message, body, credentials, _options.Region, now, "bedrock-mantle"); token.ThrowIfCancellationRequested();
    }
    /// <summary>不能在已绑定来源身份下换来源配置。</summary>
    private void SourceUnchanged()
    {
        if (_source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Mantle 凭据来源配置已经改变，应注册新供应商。");
    }
    /// <summary>固定错误不回显原生头值或凭据。</summary>
    private static void CheckValue(string value)
    {
        if (value is null || value.Any(c => c is < (char)32 or > (char)126)) throw new ArgumentException("Mantle 原生头值必须是无控制字符的 ASCII 文本。");
    }
}
