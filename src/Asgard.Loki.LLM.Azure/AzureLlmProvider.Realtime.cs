using System.Collections.ObjectModel;
using System.Text.Json;
using Asgard.Loki.LLM.Azure.Internal;

namespace Asgard.Loki.LLM.Azure;

/// <summary>Azure 自己的原生 Realtime 握手和认证，沿用 LLM 原生双工契约，不借用 Responses 状态。</summary>
public sealed partial class AzureLlmProvider
{
    private readonly AzureRealtimeOptions _realtimeOptions = new();
    private readonly IAzureRealtimeWebSocketFactory _realtimeFactory = new DefaultAzureRealtimeWebSocketFactory();
    private readonly string? _realtimeIdentity;
    private readonly string _realtimeFactoryIdentity;
    /// <summary>显式配置 Realtime；保留原 HTTP 构造签名和默认绑定，借用工厂/认证源/时钟。</summary>
    public AzureLlmProvider(IHttpClientFactory factory, AzureLlmOptions? options, IAzureLlmTokenSource? tokenSource,
        AzureRealtimeOptions realtimeOptions, IAzureRealtimeWebSocketFactory? realtimeFactory = null,
        string id = "azure", string httpClientName = "Asgard.Loki.LLM.Azure", TimeProvider? timeProvider = null)
        : this(factory, options, tokenSource, id, httpClientName, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(realtimeOptions); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(realtimeOptions.MaximumMessageBytes, 0);
        _realtimeOptions = realtimeOptions with { }; _realtimeFactory = realtimeFactory ?? _realtimeFactory;
        _realtimeFactoryIdentity = _realtimeFactory.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(_realtimeFactoryIdentity); AzureJson.Text(_realtimeFactoryIdentity);
        _realtimeIdentity = ":azure-realtime-v1:" + JsonSerializer.Serialize(new { Options = _realtimeOptions, Factory = _realtimeFactoryIdentity });
    }
    /// <inheritdoc />
    public async Task<IRealtimeConnection> OpenRealtimeAsync(LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Azure Realtime 未配置所选认证方式。");
        var root = context.Target.BaseAddress; ArgumentNullException.ThrowIfNull(root);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0) throw new ArgumentException("Azure Realtime 根必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Target.UpstreamModel); AzureJson.Text(context.Target.UpstreamModel);
        var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/realtime"); var query = _options.ApiStyle == AzureLlmApiStyle.Dated
            ? "api-version=" + Uri.EscapeDataString(_options.ApiVersion!) + "&deployment=" + Uri.EscapeDataString(context.Target.UpstreamModel)
            : "model=" + Uri.EscapeDataString(context.Target.UpstreamModel) + (_options.ApiVersion is null ? "" : "&api-version=" + Uri.EscapeDataString(_options.ApiVersion));
        var address = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws", Port = endpoint.IsDefaultPort ? -1 : endpoint.Port, Query = query }.Uri;
        DateTimeOffset? expiry = null; var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Validate()
        {
            if (_realtimeFactory.BindingIdentity != _realtimeFactoryIdentity) throw new InvalidOperationException("Azure Realtime 工厂配置已变化，应注册新供应商。");
            if (context.Credential.Kind != LlmCredentialKind.External) return;
            SourceUnchanged(); if (expiry is null || expiry <= _time.GetUtcNow()) throw new InvalidOperationException("Azure Realtime 令牌已到期，不能在本连接刷新或重新握手。");
        }
        if (context.Credential.Kind == LlmCredentialKind.ApiKey)
        {
            var key = context.Credential.ApiKey; if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is < '!' or > '~')) throw new ArgumentException("Azure API Key 必须为无空白/控制字符的 ASCII 文本。"); headers.Add("api-key", key);
        }
        else
        {
            SourceUnchanged();
            if (_realtimeFactory.BindingIdentity != _realtimeFactoryIdentity) throw new InvalidOperationException("Azure Realtime 工厂配置已变化。");
            var access = await _tokenSource!.GetTokenAsync(context.Credential.PrincipalIdentity, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Azure 认证源未返回令牌。");
            cancellationToken.ThrowIfCancellationRequested(); SourceUnchanged();
            if (access.PrincipalIdentity != context.Credential.PrincipalIdentity || access.AccessToken.Any(c => c is < '!' or > '~')) throw new InvalidOperationException("Azure Realtime 令牌主体或 Bearer 文本无效。");
            expiry = access.ExpiresOn; headers.Add("Authorization", "Bearer " + access.AccessToken);
        }
        Validate(); cancellationToken.ThrowIfCancellationRequested();
        var socket = await _realtimeFactory.ConnectAsync(address, new ReadOnlyDictionary<string, string>(headers), cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Azure Realtime 工厂没有返回 socket。");
        try { cancellationToken.ThrowIfCancellationRequested(); Validate(); return new AzureRealtimeConnection(new(socket, _realtimeOptions.MaximumMessageBytes, Validate), context.Metadata); }
        catch { socket.Dispose(); throw; }
    }
}
