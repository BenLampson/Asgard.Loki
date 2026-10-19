using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Embedding.Jina.Internal;

namespace Asgard.Loki.Embedding.Jina;

/// <summary>单次调用 Jina 原生嵌入接口，保留媒体与多向量，不重试、切换目标或解释文件。</summary>
public sealed class JinaEmbeddingAdapter : IEmbeddingAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly JinaEmbeddingOptions _options;
    private readonly Uri _endpoint;

    /// <summary>校验完整目标，不创建 HTTP 客户端、查询模型或借用其他能力的 Key。</summary>
    public JinaEmbeddingAdapter(IHttpClientFactory factory, JinaEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("Jina API 根地址必须为无凭据、查询或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "embeddings");
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = JinaEmbeddingPayload.Create(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
            is "text/event-stream" or "application/x-ndjson" or "application/ndjson")
            throw new InvalidDataException("Jina 同步嵌入需要单个完整 JSON 响应。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new EmbeddingUpstreamException("Jina 嵌入调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (JinaEmbeddingResponse.IsError(root)) throw new EmbeddingUpstreamException("Jina 返回了业务错误而非嵌入。", (int)response.StatusCode, body);
        _ = JinaEmbeddingResponse.Read(root, payload); cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
