using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Rerank.Cohere.Internal;

namespace Asgard.Loki.Rerank.Cohere;

/// <summary>执行显式版本的单次 Cohere 原生重排，不重试、更换目标或转换文档内容。</summary>
public sealed class CohereRerankAdapter : IRerankAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly CohereRerankOptions _options;
    private readonly Uri _endpoint;

    /// <summary>验证完整固定目标，不访问网络；外部 HTTP 工厂保持借用关系。</summary>
    public CohereRerankAdapter(IHttpClientFactory factory, CohereRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        if (options.ApiVersion is not (CohereRerankApiVersion.V1 or CohereRerankApiVersion.V2)) throw new ArgumentException("必须显式选择 Cohere 原生 API 版本。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("重排服务根地址必须是无凭据、查询参数或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), options.ApiVersion == CohereRerankApiVersion.V1 ? "v1/rerank" : "v2/rerank");
        _factory = factory; _options = options;
    }

    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = CohereRerankPayload.Create(request, _options.Model, _options.ApiVersion);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.ClientName is not null) message.Headers.Add("X-Client-Name", _options.ClientName);
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cohere 原生重排只接受完整 JSON，不接受 SSE。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new RerankUpstreamException("Cohere 重排调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
            || !root.TryGetProperty("results", out _) && root.TryGetProperty("message", out _)))
            throw new RerankUpstreamException("上游返回了重排错误而非排名。", (int)response.StatusCode, body);
        CohereRerankResponse.Validate(root, payload, _options.ApiVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
