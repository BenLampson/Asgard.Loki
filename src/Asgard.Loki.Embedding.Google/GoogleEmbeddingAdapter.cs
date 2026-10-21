using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Embedding.Google.Internal;

namespace Asgard.Loki.Embedding.Google;

/// <summary>执行显式配置的原生单条/同步批嵌入，不推断操作、重试、切换目标或创建后台批任务。</summary>
public sealed class GoogleEmbeddingAdapter : IEmbeddingAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly GoogleEmbeddingOptions _options;
    private readonly Uri _endpoint;
    private readonly string _resource;

    /// <summary>校验 API Key Developer API 完整目标；Vertex OAuth/签名不属于此适配器。</summary>
    public GoogleEmbeddingAdapter(IHttpClientFactory factory, GoogleEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        if (!Enum.IsDefined(options.Operation)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Model.Contains('/') || options.Model.Contains('\\') || options.Model.Any(char.IsWhiteSpace))
            throw new ArgumentException("实际模型必须是没有 models/ 前缀的单段 ID。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("Google API 根地址必须是无凭据、查询或片段的 HTTP(S) 绝对地址。", nameof(options));
        _resource = "models/" + options.Model;
        var method = options.Operation == GoogleEmbeddingOperation.EmbedContent ? "embedContent" : "batchEmbedContents";
        _endpoint = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "models/" + Uri.EscapeDataString(options.Model) + ":" + method);
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = GoogleEmbeddingPayload.Create(request, _options.Operation, _resource);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Add("x-goog-api-key", _options.ApiKey); message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(payload.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var client = _factory.CreateClient(_options.HttpClientName);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("同步 Google 嵌入不接受 SSE 响应。");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode) throw new EmbeddingUpstreamException("Google 嵌入调用失败。", (int)response.StatusCode, body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new EmbeddingUpstreamException("Google 返回了业务错误而非数值向量。", (int)response.StatusCode, body);
        _ = GoogleEmbeddingResponse.Read(root, _options.Operation, payload.Dimensions); cancellationToken.ThrowIfCancellationRequested();
        return new(root, new(request.Model, _options.Model, Id));
    }
}
