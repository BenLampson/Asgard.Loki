using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.NewAPI.Internal;

namespace Asgard.Loki.LLM.NewAPI;

/// <summary>固定完整目标和明确凭据的单次原生 alpha/search，不执行本地搜索或后台流程。</summary>
public sealed class NewApiSearchClient
{
    private readonly IHttpClientFactory _factory;
    private readonly NewApiSearchOptions _options;
    private readonly string _key;
    private readonly Uri _endpoint;
    /// <summary>借用 HTTP 工厂和显式凭据；不发现、刷新或解释凭据。</summary>
    public NewApiSearchClient(IHttpClientFactory factory, NewApiSearchOptions options, LlmCredential credential)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(credential);
        if (credential.Kind != (options.Authentication == NewApiSearchAuthentication.None ? LlmCredentialKind.Anonymous : LlmCredentialKind.ApiKey)) throw new ArgumentException("搜索认证与明确凭据种类不匹配，不借用外部账户或自动降级。");
        foreach (var text in new[] { options.UpstreamModel, options.TargetId, options.Version, options.HttpClientName, credential.Id }) { ArgumentException.ThrowIfNullOrWhiteSpace(text); NewApiSearchInput.Text(text); }
        var key = credential.Kind == LlmCredentialKind.ApiKey ? credential.ApiKey : "";
        if (credential.Kind == LlmCredentialKind.ApiKey) NewApiSearchInput.Text(key);
        NewApiSearchConfiguredEndpoint.Validate(options.Transport ?? new(), options.Authentication);
        if (options.Transport is null && options.Authentication == NewApiSearchAuthentication.Bearer) _ = new AuthenticationHeaderValue("Bearer", key);
        else _ = NewApiSearchConfiguredCall.CredentialValue(options, key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        _options = options with { BaseAddress = NewApiSearchInput.Root(options.BaseAddress), Transport = options.Transport is null ? null : options.Transport with { } };
        var transport = _options.Transport ?? new(); _endpoint = NewApiSearchConfiguredEndpoint.Create(_options.BaseAddress, transport.SearchPath, transport, null, NewApiSearchAuthentication.None);
        _factory = factory; _key = key; Metadata = new(_options.TargetId, _options.BaseAddress, _options.UpstreamModel, credential.Id, _options.Version) { Authentication = ConfiguredTransport ? _options.Authentication : null, Transport = ConfiguredTransport ? transport : null };
    }
    private bool ConfiguredTransport => _options.Transport is not null || _options.Authentication != NewApiSearchAuthentication.Bearer;
    /// <summary>固定完整搜索目标，不创建模型 Session、响应链或隐藏历史。</summary>
    public NewApiSearchMetadata Metadata { get; }
    /// <summary>冻结原生参数并调用一次上游；model 来自固定目标，id/stream 等参数保持原值。</summary>
    public async Task<NewApiSearchResult> SearchAsync(JsonObject parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var body = NewApiSearchInput.Body(parameters, _options.UpstreamModel, _options.MaximumRequestBytes);
        byte[] bytes; HttpStatusCode status; string? type; HttpResponseMessage? actual = null;
        var configured = ConfiguredTransport ? new NewApiSearchConfiguredCall(body, _options, _key) : null;
        using (var message = configured?.Request ?? new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            if (configured is null)
            {
                message.Headers.Authorization = new("Bearer", _key); message.Headers.Host = _endpoint.Authority;
                message.Headers.Accept.Add(new("application/json")); message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json");
            }
            using (var client = _factory.CreateClient(_options.HttpClientName))
            {
                cancellationToken.ThrowIfCancellationRequested(); configured?.BeforeSend(client);
                using (var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    actual = response; configured?.Verify(response); status = response.StatusCode; type = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
                    long? length;
                    try { length = response.Content.Headers.ContentLength; }
                    catch (FormatException error) { throw new InvalidDataException("搜索响应声明长度无效。", error); }
                    if (response.Content.Headers.NonValidated.Contains("Content-Length") && length is null || length < 0 || length > _options.MaximumResponseBytes) throw new InvalidDataException("搜索响应声明长度无效或超过上限。");
                    bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); configured?.Verify(actual);
        if ((int)status is < 200 or >= 300) throw new LlmUpstreamException("New API 原生搜索 HTTP 调用失败。", status, bytes, type);
        var result = new NewApiSearchResult(_endpoint, status, type, bytes, Metadata); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
