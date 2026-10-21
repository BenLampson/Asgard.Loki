using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Image.XAI.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.XAI;

/// <summary>原生 xAI JSON 图片编辑；既有 Image 编辑路由不需要媒体或协议转换。</summary>
public sealed class XaiImageEditAdapter : IImageEditAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly XaiImageEditOptions _options;
    /// <summary>绑定明确的编辑地址、实际模型和独立凭据，工厂由应用拥有。</summary>
    public XaiImageEditAdapter(IHttpClientFactory factory, XaiImageEditOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); XaiImageJson.Text(value); }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("xAI 编辑凭据必须为无空白/控制字符的 ASCII 文本。");
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("https" or "http") || options.Endpoint.UserInfo.Length != 0 || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0)
            throw new ArgumentException("xAI 编辑地址必须是无凭据、查询和片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
        => SendAsync(XaiImageEditPayload.Prepare(request, _options, cancellationToken), request.Model, cancellationToken);
    /// <summary>xAI 原生 JSON 不能由 multipart 自动转换；在读取或释放借用上传前失败。</summary>
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new NotSupportedException("xAI 图片编辑仅支持原生 JSON 引用，不转换 multipart 上传。"); }
    /// <summary>每次只提交一个原生请求；全部资源结束和原生容器验证之后才返回结果。</summary>
    private async Task<ImageGenerationResult> SendAsync(byte[] body, string publicModel, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint))
        {
            message.Headers.Authorization = new("Bearer", _options.ApiKey); message.Headers.Accept.ParseAdd("application/json");
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json");
            using var client = _factory.CreateClient(_options.HttpClientName); client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Remove("api-key"); token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _options.Endpoint)) throw new InvalidDataException("xAI 编辑实际响应目标/方法与绑定不符。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new ImageEditUpstreamException("xAI 图片编辑 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("xAI 完整编辑结果必须为 UTF-8 JSON。");
        var root = XaiImageJson.Parse(bytes);
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new ImageEditUpstreamException("xAI 图片编辑原生 API 错误。", status, bytes, type);
        XaiImageJson.Result(root); token.ThrowIfCancellationRequested(); return new(root, new(publicModel, _options.Model, Id));
    }
}
