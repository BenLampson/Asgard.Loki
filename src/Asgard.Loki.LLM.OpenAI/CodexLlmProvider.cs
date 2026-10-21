using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>显式 Codex 后端 Responses/compact 供应商；复用本包原生传输，不引入登录或代理业务。</summary>
public sealed class CodexLlmProvider : IResponsesAdapter, IResponsesCompactionAdapter, ILlmCredentialSupport
{
    private readonly IHttpClientFactory _factory;
    private readonly string _clientName;
    private readonly CodexLlmOptions _options;
    private readonly CodexAuthentication _auth;
    private readonly OpenAiHttpOptions _http;
    /// <summary>借用 HTTP 工厂、一次取令牌的外部来源和时钟；账户/策略进入完整 Session 绑定。</summary>
    public CodexLlmProvider(IHttpClientFactory factory, CodexLlmOptions options, ICodexAccessTokenSource tokenSource,
        string id = "codex", string httpClientName = "Asgard.Loki.LLM.OpenAI.Codex", TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options); ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        OpenAiHttpJson.Text(id); OpenAiHttpJson.Text(httpClientName); _auth = new(options.Account, tokenSource, timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTerminalEvents, 0);
        _options = options with { Account = _auth.Account }; _factory = factory; _clientName = httpClientName; Id = id;
        _http = new() { MaximumBodyBytes = options.MaximumResponseBytes, MaximumBufferedTerminalEvents = options.MaximumBufferedTerminalEvents };
    }
    /// <inheritdoc />
    public string Id { get; }
    /// <inheritdoc />
    public string BindingIdentity => GetType().AssemblyQualifiedName + "\ncodex-native-http-v1\n" + JsonSerializer.Serialize(new { Options = _options, Client = _clientName, Authentication = _auth.BindingIdentity });
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.External;
    /// <inheritdoc />
    public async Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync(request, context, false, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <inheritdoc />
    public async Task<ResponseCompaction> CompactAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
        => new(await PostAsync(request, context, true, cancellationToken).ConfigureAwait(false), context.Metadata);
    /// <summary>共享原生 Responses 生命周期、完整未知字段、资源与终态约束，不拼接工具或推理。</summary>
    public async IAsyncEnumerable<ResponseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = await PrepareAsync(request, context, false, true, cancellationToken).ConfigureAwait(false);
        await foreach (var frame in OpenAiHttpTransport.StreamAsync(call.Request, _factory, _clientName, _http, true, cancellationToken, call.BeforeSend, call.Verify, true).ConfigureAwait(false))
        { using var data = JsonDocument.Parse(frame.Data); yield return new(data.RootElement, context.Metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds); }
    }
    /// <summary>完整 JSON 在全部拥有资源关闭与外部身份复查后交付。</summary>
    private async Task<JsonElement> PostAsync(JsonObject payload, LlmAdapterContext context, bool compact, CancellationToken token)
    {
        var call = await PrepareAsync(payload, context, compact, false, token).ConfigureAwait(false);
        return await OpenAiHttpTransport.PostAsync(call.Request, _factory, _clientName, _http, token, call.BeforeSend, call.Verify, true).ConfigureAwait(false);
    }
    /// <summary>原生模型和方法流标志归明确目标/方法所有，其他字段原样快照到首次异步认证之前。</summary>
    private Task<CodexHttpCall> PrepareAsync(JsonObject payload, LlmAdapterContext context, bool compact, bool stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Target); ArgumentNullException.ThrowIfNull(context.Credential);
        if (!SupportsCredential(context.Credential.Kind)) throw new ArgumentException("Codex 仅支持显式 External 主体来源，不借用 Platform API Key。");
        if (context.Target.ProviderId != Id || context.Target.CredentialId != context.Credential.Id) throw new ArgumentException("Codex 上下文不属于所选供应商或凭据身份。");
        OpenAiHttpJson.Text(context.Credential.PrincipalIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Target.UpstreamModel); OpenAiHttpJson.Text(context.Target.UpstreamModel);
        var endpoint = CodexInput.Endpoint(context.Target.BaseAddress, compact ? "responses/compact" : "responses");
        var body = CodexInput.Body(payload, context.Target.UpstreamModel, compact ? null : stream, _options.MaximumRequestBytes, compact);
        return _auth.PrepareAsync(endpoint, body, context.Credential.PrincipalIdentity, stream, token);
    }
}
