using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Embedding.Ollama.Internal;

namespace Asgard.Loki.Embedding.Ollama;

/// <summary>执行单次原生 embed，不启动服务器、拉取模型、重试、切换目标或转换 OpenAI 外壳。</summary>
public sealed class OllamaEmbeddingAdapter : IEmbeddingAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OllamaEmbeddingOptions _options;
    private readonly Uri _endpoint;

    /// <summary>校验完整固定目标，不创建客户端或推断认证。</summary>
    public OllamaEmbeddingAdapter(IHttpClientFactory factory, OllamaEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        if (options.ApiKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("Ollama API 根地址必须为无凭据、查询或片段的 HTTP(S) 绝对地址。", nameof(options));
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "embed");
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = OllamaEmbeddingPayload.Create(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (_options.ApiKey is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
            is "text/event-stream" or "application/x-ndjson" or "application/ndjson")
            throw new InvalidDataException("原生 embed 需要单个完整 JSON 响应，不能使用流事件。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new EmbeddingUpstreamException("Ollama 嵌入调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new EmbeddingUpstreamException("Ollama 返回了业务错误而非原生嵌入。", (int)response.StatusCode, body);
        _ = OllamaEmbeddingResponse.Read(root, payload.Count, payload.Dimensions); cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
