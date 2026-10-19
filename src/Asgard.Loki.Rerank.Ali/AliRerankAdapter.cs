using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Rerank.Ali.Internal;

namespace Asgard.Loki.Rerank.Ali;

/// <summary>单次调用 DashScope 原生嵌套重排接口，不重试、切换地域或扁平化文档。</summary>
public sealed class AliRerankAdapter : IRerankAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly AliRerankOptions _options;
    private readonly Uri _endpoint;

    /// <summary>校验完整固定目标，不创建 HTTP 客户端或读取外部身份。</summary>
    public AliRerankAdapter(IHttpClientFactory factory, AliRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("DashScope API 根地址必须为无凭据、查询或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "services/rerank/text-rerank/text-rerank");
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = AliRerankPayload.Create(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
            is "text/event-stream" or "application/x-ndjson" or "application/ndjson")
            throw new InvalidDataException("DashScope 原生重排需要单个完整 JSON 响应。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new RerankUpstreamException("DashScope 重排调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (AliRerankResponse.IsError(root)) throw new RerankUpstreamException("DashScope 返回了业务错误而非原生排名。", (int)response.StatusCode, body);
        AliRerankResponse.Validate(root, payload); cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
