using System.Net.Http.Headers;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Midjourney.Internal;

/// <summary>固定图片代理目标的单次有界原生 HTTP 交换，不重试、重定向或下载媒体。</summary>
internal sealed class MidjourneyImageHttp
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    internal MidjourneyImageOptions Options { get; }
    internal MidjourneyImageHttp(IHttpClientFactory factory, MidjourneyImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.HttpClientName }) { ArgumentException.ThrowIfNullOrWhiteSpace(value); MidjourneyImageJson.Text(value); }
        if (options.ApiSecret is { } secret && (secret.Length == 0 || secret.Any(c => c is < '!' or > '~'))) throw new ArgumentException("代理图片密钥必须为非空无空白/控制字符的 ASCII 文本。");
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%')) throw new ArgumentException("图片代理需要明确、无凭据/查询/转义路径/片段的 HTTP(S) 根地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; Options = options; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/");
    }
    /// <summary>先结束所有 HTTP 资源和取消边界，再解释原生 JSON 或整体错误。</summary>
    internal async Task<MidjourneyImageResponse> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken token)
    {
        byte[] bytes; int status; string? type; var address = new Uri(_root, path);
        using (var message = new HttpRequestMessage(method, address))
        {
            message.Headers.Accept.ParseAdd("application/json"); if (Options.ApiSecret is { } secret) message.Headers.Add("mj-api-secret", secret);
            if (body is not null) { message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); }
            using var client = _factory.CreateClient(Options.HttpClientName);
            foreach (var name in new[] { "Authorization", "mj-api-secret", "api-key", "x-api-key", "Cookie" }) client.DefaultRequestHeaders.Remove(name);
            token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != method || actual.RequestUri != address)) throw new InvalidDataException("图片代理实际响应目标或方法与固定调用不一致。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, Options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new ImageUpstreamException("图片代理 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("图片代理结果必须为 UTF-8 原生 JSON。");
        var data = MidjourneyImageJson.Parse(bytes); token.ThrowIfCancellationRequested(); return new(data, new(status, bytes, type));
    }
}
