using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Image.MiniMax.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.MiniMax;

/// <summary>固定 MiniMax image_generation 的原生 JSON 调用，不重试、转换格式或下载图片。</summary>
public sealed class MiniMaxImageAdapter : IImageGenerationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly MiniMaxImageOptions _options;
    /// <summary>绑定独立模型、原生完整地址和凭据，借用 HTTP 工厂。</summary>
    public MiniMaxImageAdapter(IHttpClientFactory factory, MiniMaxImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); MiniMaxImageJson.Text(value); }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("MiniMax 图片凭据必须是无空白/控制字符的 ASCII 文本。");
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https") || options.Endpoint.UserInfo.Length != 0
            || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0) throw new ArgumentException("MiniMax 图片地址必须为无凭据、查询和片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        => SendAsync(MiniMaxImagePayload.Prepare(request, _options, cancellationToken), request.Model, cancellationToken);
    /// <summary>未提供原生流契约，显式流入口在任何网络调用前失败。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("MiniMax image_generation 仅返回完整原生 JSON。"); }
    /// <summary>完整响应通过字节/长度、HTTP 所有权和取消检查后，保留原生业务状态及图像结果。</summary>
    private async Task<ImageGenerationResult> SendAsync(byte[] body, string publicModel, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint))
        {
            message.Headers.Authorization = new("Bearer", _options.ApiKey); message.Headers.Accept.ParseAdd("application/json");
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json");
            using var client = _factory.CreateClient(_options.HttpClientName);
            client.DefaultRequestHeaders.Authorization = null; client.DefaultRequestHeaders.Remove("api-key"); token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _options.Endpoint)) throw new InvalidDataException("MiniMax 图片实际响应目标/方法与固定目标不一致。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new ImageUpstreamException("MiniMax 图片 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("MiniMax 完整图片结果必须为 UTF-8 JSON。");
        var root = MiniMaxImageJson.Parse(bytes);
        if (!root.TryGetProperty("base_resp", out var state) || state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("status_code", out var code)
            || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var value)) throw new InvalidDataException("MiniMax 图片结果缺少原生整数 base_resp.status_code。");
        if (value != 0) throw new ImageUpstreamException("MiniMax 图片原生业务错误。", status, bytes, type);
        var images = MiniMaxImageJson.Images(root); token.ThrowIfCancellationRequested(); return new(root, new(publicModel, _options.Model, Id), images) { RawResponse = new(status, bytes, type) };
    }
}
