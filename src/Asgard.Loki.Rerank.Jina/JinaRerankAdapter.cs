using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Rerank.Jina.Internal;

namespace Asgard.Loki.Rerank.Jina;

/// <summary>执行一次 Jina 原生重排，保留文本、多模态和 listwise 结构，不重试或更换目标。</summary>
public sealed class JinaRerankAdapter : IRerankAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly JinaRerankOptions _options;
    private readonly Uri _endpoint;

    /// <summary>验证固定完整目标；HTTP 工厂、适配器和媒体内容均不由 SDK 接管。</summary>
    public JinaRerankAdapter(IHttpClientFactory factory, JinaRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("重排基础地址必须是无凭据、查询参数或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "rerank"); _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = JinaRerankPayload.Create(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Jina 原生重排只接受完整 JSON，不接受 SSE。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new RerankUpstreamException("Jina 重排调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
            || !root.TryGetProperty("results", out _) && root.TryGetProperty("detail", out _)))
            throw new RerankUpstreamException("上游返回了 Jina 错误而非排名。", (int)response.StatusCode, body);
        JinaRerankResponse.Validate(root, payload); cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
