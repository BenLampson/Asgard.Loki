using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Embedding.OpenAI.Internal;

namespace Asgard.Loki.Embedding.OpenAI;

/// <summary>执行单次原生 POST embeddings，保留批输入、编码、响应和错误，不自动重试。</summary>
public sealed class OpenAiEmbeddingAdapter : IEmbeddingAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiEmbeddingOptions _options;
    private readonly Uri _endpoint;

    /// <summary>校验完整目标，不访问网络或借用其他能力的凭据。</summary>
    public OpenAiEmbeddingAdapter(IHttpClientFactory factory, OpenAiEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("嵌入基础地址必须是无凭据、查询参数或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "embeddings");
        _factory = factory; _options = options;
    }

    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = OpenAiEmbeddingPayload.Create(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
        if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原生嵌入调用不接受 SSE 响应。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new EmbeddingUpstreamException("OpenAI 嵌入调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new EmbeddingUpstreamException("上游返回了嵌入错误而非向量。", (int)response.StatusCode, body);
        OpenAiEmbeddingResponse.Validate(root, payload.Count, payload.Dimensions, payload.Encoding);
        cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
