using System.Net.Http.Headers;
using Asgard.Loki.Embedding.Azure.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Embedding.Azure;

/// <summary>Azure 部署路径、显式版本和独立 api-key 的一次原生嵌入调用。</summary>
public sealed class AzureEmbeddingAdapter : IEmbeddingAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly AzureEmbeddingOptions _options;
    private readonly Uri _address;
    /// <summary>验证完整固定目标，借用 HTTP 工厂，不读取其他能力的配置或凭据。</summary>
    public AzureEmbeddingAdapter(IHttpClientFactory factory, AzureEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.Deployment, options.ApiVersion, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); AzureEmbeddingJson.Text(value); }
        if (options.BodyModel is { } bodyModel) { ArgumentException.ThrowIfNullOrWhiteSpace(bodyModel); AzureEmbeddingJson.Text(bodyModel); }
        if (options.ApiKey.Any(c => c is < '!' or > '~') || options.ApiVersion.Any(char.IsControl)) throw new ArgumentException("Azure 嵌入凭据或版本包含无效字符。");
        if (options.Deployment is "." or ".." || options.Deployment.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '/' or '\\' or '%')) throw new ArgumentException("Azure 嵌入部署必须为无歧义的单一路径段。");
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https") || options.Endpoint.UserInfo.Length != 0
            || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0 || options.Endpoint.AbsolutePath.Contains('%')) throw new ArgumentException("Azure 嵌入资源必须为无凭据、查询、转义路径和片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; _options = options; _address = new(options.Endpoint.AbsoluteUri.TrimEnd('/') + "/openai/deployments/" + Uri.EscapeDataString(options.Deployment)
            + "/embeddings?api-version=" + Uri.EscapeDataString(options.ApiVersion));
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <summary>冻结原生请求，公共模型名只用于结果元数据，不泄露到原生部署或 body model。</summary>
    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var payload = AzureEmbeddingPayload.Prepare(request, _options, cancellationToken);
        return SendAsync(payload, request.Model, cancellationToken);
    }
    /// <summary>先完成原始字节/长度与全部 HTTP 资源释放，再判断原生结果并检查取消。</summary>
    private async Task<EmbeddingResult> SendAsync((byte[] Body, int Count, int? Dimensions, string? Encoding) payload, string publicModel, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _address))
        {
            message.Headers.Add("api-key", _options.ApiKey); message.Headers.Accept.ParseAdd("application/json");
            message.Content = new ByteArrayContent(payload.Body); message.Content.Headers.ContentType = new("application/json");
            using var client = _factory.CreateClient(_options.HttpClientName);
            foreach (var name in new[] { "Authorization", "api-key", "x-api-key", "OpenAI-Organization", "OpenAI-Project" }) client.DefaultRequestHeaders.Remove(name);
            token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _address)) throw new InvalidDataException("Azure 嵌入实际响应目标或方法与固定配置不一致。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new EmbeddingUpstreamException("Azure 嵌入 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Azure 嵌入结果必须是完整 UTF-8 JSON，不接受 SSE。");
        var data = AzureEmbeddingJson.Parse(bytes);
        if (data.TryGetProperty("error", out var error) && error.ValueKind != System.Text.Json.JsonValueKind.Null) throw new EmbeddingUpstreamException("Azure 返回原生嵌入 API 错误。", status, bytes, type);
        AzureEmbeddingResponse.Validate(data, payload.Count, payload.Dimensions, payload.Encoding); token.ThrowIfCancellationRequested();
        return new(data, new(publicModel, _options.Deployment, Id));
    }
}
