using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.Tencent.Internal;

namespace Asgard.Loki.LLM.Tencent;

/// <summary>腾讯混元原生 TC3 生成；完整目标与稳定主体由 LLM Session 绑定。</summary>
public sealed partial class TencentHunyuanProvider : IHunyuanAdapter, ILlmCredentialSupport, ILlmProtocolSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly ITencentHunyuanCredentialSource _source;
    private readonly string _sourceIdentity;
    private readonly TencentHunyuanOptions _options;
    private readonly string _clientName;
    private readonly TimeProvider _time;
    /// <summary>借用工厂、来源和时钟，不发现凭据、解析拼接 Key、登录或刷新。</summary>
    public TencentHunyuanProvider(IHttpClientFactory factory, ITencentHunyuanCredentialSource credentialSource, TencentHunyuanOptions? options = null,
        string id = "tencent", string httpClientName = "Asgard.Loki.LLM.Tencent", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(credentialSource); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        TencentJson.Text(id); TencentJson.Text(httpClientName); options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        if (options.Region is { } region && (region.Length == 0 || region.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))) throw new ArgumentException("腾讯区域必须是显式小写 ASCII 区域标识。");
        if (options.SigningHost is { } host && (host.Any(c => c is < '!' or > '~') || !Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || !host.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("签名 Host 必须仅含有效原生主机及可选端口。");
        _sourceIdentity = credentialSource.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); TencentJson.Text(_sourceIdentity);
        _factory = factory; _source = credentialSource; _options = options with { SigningHost = options.SigningHost?.ToLowerInvariant() }; _clientName = httpClientName; _time = timeProvider ?? TimeProvider.System; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":hunyuan-tc3-v1:" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName, Source = _sourceIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.External;
    /// <inheritdoc />
    public bool SupportsProtocol(LlmProtocol protocol) => protocol == LlmProtocol.Hunyuan;
    /// <summary>完整读取原生 Response 信封；EOF、关闭和最终认证/取消检查完成后才交付。</summary>
    public async Task<HunyuanCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        var token = cancellationToken; token.ThrowIfCancellationRequested(); ValidateContext(context); var endpoint = context.Target.BaseAddress;
        var body = TencentPayload.Snapshot(request, context.Target.UpstreamModel, false, _options.MaximumRequestBytes);
        var credentials = await CredentialsAsync(context, token).ConfigureAwait(false); byte[] bytes; HttpStatusCode status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
        using (var client = _factory.CreateClient(_clientName))
        {
            AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token);
            if (client.DefaultRequestHeaders.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || h.Key.StartsWith("X-TC-", StringComparison.OrdinalIgnoreCase) || new[] { "api-key", "x-api-key", "x-goog-api-key" }.Contains(h.Key, StringComparer.OrdinalIgnoreCase))) throw new InvalidOperationException("混元客户端默认头包含非本次原生调用拥有的认证或签名配置。");
            using var originalContent = new ByteArrayContent(body); message.Content = originalContent; message.Content.Headers.ContentType = new("application/json"); message.Headers.Accept.ParseAdd("application/json");
            TencentTc3.Sign(message, body, _options.SigningHost ?? endpoint.Authority, credentials, _time.GetUtcNow(), _options.Region);
            var hash = SHA256.HashData(body); var headers = message.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token);
            if (message.Method != HttpMethod.Post || message.RequestUri != endpoint || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != endpoint)
                || !ReferenceEquals(message.Content, originalContent) || message.Content.Headers.ContentType?.ToString() != "application/json"
                || !SHA256.HashData(await message.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false)).SequenceEqual(hash)
                || headers.Any(h => !message.Headers.TryGetValues(h.Key, out var values) || string.Join(",", values) != h.Value)
                || message.Headers.Any(h => !headers.ContainsKey(h.Key) && (h.Key.StartsWith("X-TC-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("混元原生实际目标、正文或认证头发生改变。");
            type = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : null;
            if (response.IsSuccessStatusCode && MediaTypeHeaderValue.TryParse(type, out var media) && media.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true) throw new InvalidDataException("混元完整调用不能接受 SSE。");
            var declared = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
            if (declared && length is null || length < 0 || length > _options.MaximumResponseBytes) throw new InvalidDataException("混元响应声明长度无效或超过上限。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, token).ConfigureAwait(false); status = response.StatusCode;
        }
        AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("腾讯混元 HTTP 调用失败。", status, bytes, type);
        JsonElement data; try { data = TencentJson.Parse(bytes); } catch (DecoderFallbackException error) { throw new InvalidDataException("混元响应不是有效 UTF-8。", error); }
        if (TencentPayload.Error(data)) throw new LlmUpstreamException("腾讯混元返回原生错误。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) && header.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("混元成功结果必须是 UTF-8 JSON。");
        TencentPayload.Complete(data); var result = new HunyuanCompletion(data, context.Metadata); AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token); return result;
    }
    /// <summary>当前原生供应商仅提供完整 JSON；不把 JSON 响应伪装成流式事件。</summary>
    public IAsyncEnumerable<HunyuanEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("此混元适配器尚不支持原生 SSE。");
    /// <summary>校验供应商、协议、服务器、实际模型与 External 主体，不按 Key 内容推测协议。</summary>
    private void ValidateContext(LlmAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(context); var target = context.Target; ArgumentNullException.ThrowIfNull(target); var endpoint = target.BaseAddress;
        if (target.ProviderId != Id || !target.Protocols.HasFlag(LlmProtocol.Hunyuan) || context.Credential.Kind != LlmCredentialKind.External) throw new ArgumentException("混元原生调用要求匹配供应商、Hunyuan 协议及 External 主体。");
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) throw new ArgumentException("混元目标必须是无凭据、查询或片段的显式 HTTP(S) 地址。");
        if (_options.SigningHost is null && endpoint.Authority.Any(c => c is < '!' or > '~')) throw new ArgumentException("混元签名 Host 必须为 ASCII；可显式配置 SigningHost。");
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel); TencentJson.Text(target.UpstreamModel);
    }
    /// <summary>每次显式调用仅获取一次不可变凭据，等待期间来源配置不得漂移。</summary>
    private async ValueTask<TencentHunyuanCredentials> CredentialsAsync(LlmAdapterContext context, CancellationToken token)
    {
        SourceUnchanged(); var credentials = await _source.GetCredentialsAsync(context.Credential.PrincipalIdentity!, token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(credentials); AssertIdentity(credentials, context.Credential.PrincipalIdentity!, token); return credentials;
    }
    /// <summary>来源身份、逻辑主体及期限在每个发布边界均须保持，不重取或自动刷新。</summary>
    private void AssertIdentity(TencentHunyuanCredentials credentials, string principal, CancellationToken token)
    { token.ThrowIfCancellationRequested(); SourceUnchanged(); if (credentials.PrincipalIdentity != principal || credentials.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("混元签名凭据主体不匹配或已经到期。"); }
    /// <summary>借用来源的配置改变不能沿用现有 Session。</summary>
    private void SourceUnchanged()
    { if (_source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("混元凭据来源配置已经改变，应注册新供应商。"); }
}
