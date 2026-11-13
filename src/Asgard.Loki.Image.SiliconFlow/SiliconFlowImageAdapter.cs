using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Image.SiliconFlow.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.SiliconFlow;

/// <summary>固定 SiliconFlow images/generations 的原生 JSON 调用，不重试、转换格式或下载图片。</summary>
public sealed class SiliconFlowImageAdapter : IImageGenerationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly SiliconFlowImageOptions _options;
    /// <summary>绑定独立模型、原生完整地址和凭据，借用 HTTP 工厂。</summary>
    public SiliconFlowImageAdapter(IHttpClientFactory factory, SiliconFlowImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); SiliconFlowImageJson.Text(value); }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("SiliconFlow 图片凭据必须是无空白/控制字符的 ASCII 文本。");
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https") || options.Endpoint.UserInfo.Length != 0
            || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0) throw new ArgumentException("SiliconFlow 图片地址必须为无凭据、查询和片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        => SendAsync(SiliconFlowImagePayload.Prepare(request, _options, cancellationToken), request.Model, cancellationToken);
    /// <summary>未提供原生流契约，显式流入口在任何网络调用前失败。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("SiliconFlow images/generations 仅返回完整原生 JSON。"); }
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
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _options.Endpoint)) throw new InvalidDataException("SiliconFlow 图片实际响应目标/方法与固定目标不一致。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new ImageUpstreamException("SiliconFlow 图片 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("SiliconFlow 完整图片结果必须为 UTF-8 JSON。");
        var root = SiliconFlowImageJson.Parse(bytes);
        if (root.TryGetProperty("code", out var code))
        {
            if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var value)) throw new InvalidDataException("SiliconFlow 原生 code 必须为整数。");
            if (value != 0) throw new ImageUpstreamException("SiliconFlow 图片原生业务错误。", status, bytes, type);
        }
        if (!root.TryGetProperty("images", out _) && root.TryGetProperty("message", out var messageText) && messageText.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(messageText.GetString()))
            throw new ImageUpstreamException("SiliconFlow 图片返回原生错误消息。", status, bytes, type);
        var images = SiliconFlowImageJson.Images(root); token.ThrowIfCancellationRequested(); return new(root, new(publicModel, _options.Model, Id), images) { RawResponse = new(status, bytes, type) };
    }
}
