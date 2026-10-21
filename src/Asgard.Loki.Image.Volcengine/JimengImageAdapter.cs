using System.Net.Http.Headers;
using Asgard.Loki.Image.Volcengine.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Volcengine;

/// <summary>即梦 Visual CVProcess 同步图片调用，绑定独立 AK/SK、req_key 和根地址。</summary>
public sealed class JimengImageAdapter : IImageGenerationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly JimengImageOptions _options;
    private readonly TimeProvider _clock;
    private readonly Uri _address;
    /// <summary>验证完整原生目标，借用 HTTP 工厂和签名时钟，不发现凭据或迁移模型。</summary>
    public JimengImageAdapter(IHttpClientFactory factory, JimengImageOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.RequestKey, options.AccessKeyId, options.SecretAccessKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); JimengImageJson.Text(value); }
        if (!options.AccessKeyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || options.SecretAccessKey.Any(c => c is < '!' or > '~')
            || options.SecurityToken is { } token && (token.Length == 0 || token.Any(c => c is < '!' or > '~')))
            throw new ArgumentException("Visual 图片签名凭据格式无效。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.AbsolutePath != "/"
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0)
            throw new ArgumentException("Visual 图片地址必须为无凭据、查询和片段的 HTTPS 根地址。", nameof(options));
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; _options = options; _clock = timeProvider ?? TimeProvider.System;
        _address = new(options.BaseAddress, "?Action=CVProcess&Version=2022-08-31");
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        => SendAsync(JimengImagePayload.Prepare(request, _options, cancellationToken), request.Model, cancellationToken);
    /// <summary>固定同步原生操作不提供流，在创建 HTTP 资源之前明确失败。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("Visual CVProcess 不提供图片 SSE。"); }
    /// <summary>每次调用独立签名和拥有 HTTP 资源，完整释放后才交付原生成功结果。</summary>
    private async Task<ImageGenerationResult> SendAsync(byte[] body, string publicModel, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _address))
        {
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); message.Headers.Accept.ParseAdd("application/json");
            using var client = _factory.CreateClient(_options.HttpClientName);
            foreach (var name in new[] { "Authorization", "Host", "X-Date", "X-Content-Sha256", "X-Security-Token", "api-key", "x-api-key" }) client.DefaultRequestHeaders.Remove(name);
            token.ThrowIfCancellationRequested(); JimengImageSigner.Sign(message, body, _options, _clock.GetUtcNow()); token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _address)) throw new InvalidDataException("Visual 图片实际响应目标/方法与完整目标不一致。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new ImageUpstreamException("Visual 图片 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Visual 图片完整结果必须为 UTF-8 JSON。");
        var root = JimengImageJson.Parse(bytes); JimengImageJson.Success(root, status, bytes, type);
        var images = JimengImageJson.Images(root); token.ThrowIfCancellationRequested();
        return new(root, new(publicModel, _options.RequestKey, Id), images);
    }
}
