using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>显式固定账户的单次原生 alpha/search；不执行本地搜索、网页获取或代理后台。</summary>
public sealed class CodexSearchClient
{
    private readonly IHttpClientFactory _factory;
    private readonly CodexSearchOptions _options;
    private readonly LlmCredential _credential;
    private readonly CodexAuthentication _auth;
    private readonly Uri _endpoint;
    /// <summary>借用工厂、External 凭据身份、来源和时钟；不读取其他项目或登录配置。</summary>
    public CodexSearchClient(IHttpClientFactory factory, CodexSearchOptions options, LlmCredential credential, ICodexAccessTokenSource tokenSource, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(credential);
        if (credential.Kind != LlmCredentialKind.External) throw new ArgumentException("Codex Search 需要显式 External 主体，不借用 Platform API Key。");
        OpenAiHttpJson.Text(credential.Id); OpenAiHttpJson.Text(credential.PrincipalIdentity);
        foreach (var text in new[] { options.TargetId, options.Version, options.HttpClientName }) { ArgumentException.ThrowIfNullOrWhiteSpace(text); OpenAiHttpJson.Text(text); }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _auth = new(options.Account, tokenSource, timeProvider); _endpoint = CodexInput.Endpoint(options.BaseAddress, "alpha/search");
        _options = options with { Account = _auth.Account, BaseAddress = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/") }; _factory = factory; _credential = credential;
        Metadata = new(_options.TargetId, _options.BaseAddress, _auth.Account.AccountId, credential.Id, options.Version);
    }
    /// <summary>固定服务身份，辅助搜索不虚构模型 Session 或响应链。</summary>
    public CodexSearchMetadata Metadata { get; }
    /// <summary>只调用一次原生搜索，完整保留原生参数及响应；可选 model/stream 字段保持调用者原值。</summary>
    public async Task<CodexSearchResult> SearchAsync(JsonObject parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var body = CodexInput.Body(parameters, null, null, _options.MaximumRequestBytes);
        var call = await _auth.PrepareAsync(_endpoint, body, _credential.PrincipalIdentity, false, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage? response = null; byte[] bytes; System.Net.HttpStatusCode status; string? type;
        using (call.Request)
        {
            cancellationToken.ThrowIfCancellationRequested(); call.Verify(null);
            using (var client = _factory.CreateClient(_options.HttpClientName))
            {
                cancellationToken.ThrowIfCancellationRequested(); call.BeforeSend(client);
                using (response = await client.SendAsync(call.Request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    call.Verify(response); status = response.StatusCode; type = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
                    var present = response.Content.Headers.NonValidated.Contains("Content-Length"); var length = response.Content.Headers.ContentLength;
                    if (present && length is null || length < 0 || length > _options.MaximumResponseBytes) throw new InvalidDataException("Codex Search 原始响应声明长度无效或超过预算。");
                    bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false); call.Verify(response);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); call.Verify(response);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("Codex 原生 Search HTTP 调用失败。", status, bytes, type);
        var result = new CodexSearchResult(_endpoint, status, type, bytes, Metadata); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
