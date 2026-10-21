using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Xunfei.Internal;

namespace Asgard.Loki.LLM.Xunfei;

/// <summary>显式原生星火 WebSocket 调用；凭据、完整目标和连接生命周期属于本供应商。</summary>
public sealed partial class XunfeiSparkProvider : ISparkAdapter, ILlmCredentialSupport, ILlmProtocolSupport
{
    private readonly IXunfeiSparkCredentialSource _source;
    private readonly IXunfeiSparkWebSocketFactory _factory;
    private readonly string _sourceIdentity;
    private readonly string _factoryIdentity;
    private readonly XunfeiSparkOptions _options;
    private readonly TimeProvider _time;
    /// <summary>借用来源、可选工厂与时钟；不登录、发现/刷新 Key 或按模型名选择地址。</summary>
    public XunfeiSparkProvider(IXunfeiSparkCredentialSource credentialSource, XunfeiSparkOptions options, IXunfeiSparkWebSocketFactory? webSocketFactory = null, string id = "xunfei", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentialSource); ArgumentNullException.ThrowIfNull(options); ArgumentException.ThrowIfNullOrWhiteSpace(id); SparkJson.Text(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppId);
        if (options.AppId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("星火 AppId 必须为显式 ASCII 应用标识。");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumMessageBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumEvents, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumFragmentsPerMessage, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0);
        foreach (var timeout in new[] { options.ConnectTimeout, options.CloseTimeout }) if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(options), "连接/关闭超时必须为有效的正有限计时范围。");
        _factory = webSocketFactory ?? new DefaultSparkWebSocketFactory(); _sourceIdentity = credentialSource.BindingIdentity; _factoryIdentity = _factory.BindingIdentity;
        ArgumentException.ThrowIfNullOrWhiteSpace(_sourceIdentity); ArgumentException.ThrowIfNullOrWhiteSpace(_factoryIdentity); SparkJson.Text(_sourceIdentity); SparkJson.Text(_factoryIdentity);
        _source = credentialSource; _options = options with { }; _time = timeProvider ?? TimeProvider.System; Id = id;
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + ":spark-ws-v1:" + JsonSerializer.Serialize(new { Options = _options, Source = _sourceIdentity, Factory = _factoryIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.External;
    /// <inheritdoc />
    public bool SupportsProtocol(LlmProtocol protocol) => protocol == LlmProtocol.Spark;
    /// <summary>同一原生传输只执行一次，保留全部消息和用量窗口，不提取首个文本或累计 Token。</summary>
    public async Task<SparkCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        List<SparkEvent> events = []; await foreach (var item in StreamAsync(request, context, cancellationToken).ConfigureAwait(false)) events.Add(item);
        cancellationToken.ThrowIfCancellationRequested(); ConfigurationUnchanged(); return new(events, context.Metadata);
    }
    /// <summary>检查完整目标和认证种类；只有传输协议映射 HTTP→WS、HTTPS→WSS，路径原样使用。</summary>
    private void ValidateContext(LlmAdapterContext context)
    {
        ArgumentNullException.ThrowIfNull(context); var target = context.Target; var endpoint = target.BaseAddress;
        if (target.ProviderId != Id || !target.Protocols.HasFlag(LlmProtocol.Spark) || context.Credential.Kind != LlmCredentialKind.External) throw new ArgumentException("星火原生调用要求匹配供应商、Spark 协议和 External 主体。");
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.Authority.Any(c => c is < '!' or > '~')
            || endpoint.AbsolutePath.Contains('%') || endpoint.AbsolutePath.Any(c => c is < '!' or > '~')) throw new ArgumentException("星火目标必须是无凭据/查询/片段的 HTTP(S) 地址及未转义 ASCII 原生路径。");
        ArgumentException.ThrowIfNullOrWhiteSpace(target.UpstreamModel); SparkJson.Text(target.UpstreamModel); SparkJson.Text(context.Credential.PrincipalIdentity);
    }
    /// <summary>来源/工厂稳定配置、主体、期限和取消必须在每个异步及交付边界保持有效。</summary>
    private void ValidateIdentity(XunfeiSparkCredentials credentials, LlmAdapterContext context, CancellationToken token)
    { token.ThrowIfCancellationRequested(); ConfigurationUnchanged(); if (credentials.PrincipalIdentity != context.Credential.PrincipalIdentity || credentials.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("星火凭据主体不匹配或已经到期。"); }
    /// <summary>借用依赖发生配置漂移时不得沿用现有完整 Session。</summary>
    private void ConfigurationUnchanged()
    { if (_source.BindingIdentity != _sourceIdentity || _factory.BindingIdentity != _factoryIdentity) throw new InvalidOperationException("星火凭据来源或连接工厂配置已改变，应注册新供应商。"); }
}
