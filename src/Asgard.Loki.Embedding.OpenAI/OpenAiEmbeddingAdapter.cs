using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Embedding.OpenAI.Internal;
using Asgard.Loki.Infrastructure;

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
        foreach (var value in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); OpenAiEmbeddingJson.Text(value); }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("嵌入凭据必须为无空白/控制字符的 ASCII 文本。");
        foreach (var value in new[] { options.Organization, options.Project })
            if (value is not null) { OpenAiEmbeddingJson.Text(value); if (value.Any(char.IsControl)) throw new ArgumentException("组织/项目头部不能包含控制字符。"); }
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("嵌入基础地址必须是无凭据、查询参数或片段的 HTTP(S) 绝对地址。", nameof(options));
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
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
        var requestBody = JsonSerializer.SerializeToUtf8Bytes(payload.Body); _ = OpenAiEmbeddingJson.Parse(requestBody);
        if (requestBody.Length > _options.MaxRequestBytes) throw new ArgumentException("原生嵌入请求超过本地 UTF-8 字节上限。");
        byte[] body; int status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            message.Headers.Authorization = new("Bearer", _options.ApiKey); message.Headers.Accept.ParseAdd("application/json");
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = new ByteArrayContent(requestBody); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var client = _factory.CreateClient(_options.HttpClientName);
            foreach (var name in new[] { "Authorization", "api-key", "x-api-key", "OpenAI-Organization", "OpenAI-Project" }) client.DefaultRequestHeaders.Remove(name);
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _endpoint)) throw new InvalidDataException("嵌入响应的实际目标/方法与固定配置不一致。");
            // 保留已公开的快速拒绝：成功 SSE 在正文读取前失败，不能等待无终止事件流。
            if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原生嵌入调用不接受 SSE 响应。");
            var length = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && length is null || length < 0) throw new InvalidDataException("嵌入响应声明长度无效。");
            body = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new EmbeddingUpstreamException("OpenAI 嵌入调用失败。", status, body, type);
        var root = OpenAiEmbeddingJson.Parse(body);
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new EmbeddingUpstreamException("上游返回了嵌入错误而非向量。", status, body, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("原生嵌入结果必须为 UTF-8 JSON。");
        OpenAiEmbeddingResponse.Validate(root, payload.Count, payload.Dimensions, payload.Encoding);
        cancellationToken.ThrowIfCancellationRequested(); return new(root, new(request.Model, _options.Model, Id));
    }
}
