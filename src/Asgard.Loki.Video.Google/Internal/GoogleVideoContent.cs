using System.Net;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Video.Google.Internal;

/// <summary>只负责单次调用者驱动的原生视频结果传输，目标流和工厂均由调用者拥有。</summary>
internal sealed class GoogleVideoContent(IHttpClientFactory factory, string clientName, Uri root, string key, long maximumBytes, int errorBytes, int redirects)
{
    /// <summary>先校验 URI 与流；不查任务，不根据模型构建下载地址，不缓冲完整视频。</summary>
    internal Task<long> CopyAsync(Uri videoUri, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoUri); ArgumentNullException.ThrowIfNull(destination); cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite) throw new ArgumentException("内容目标流必须可写。", nameof(destination));
        ValidateInitial(videoUri); return CopyCoreAsync(videoUri, destination, cancellationToken);
    }
    /// <summary>只接受配置根下原生下载端点，不向任意返回地址发送 API Key。</summary>
    private void ValidateInitial(Uri uri)
    {
        var prefix = root.AbsolutePath + "files/";
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Authority != root.Authority || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith(":download", StringComparison.Ordinal) || uri.Query is not ("" or "?alt=media"))
            throw new ArgumentException("视频内容 URI 必须属于配置的原生文件下载端点。");
        var segment = uri.AbsolutePath[prefix.Length..^9]; GoogleVideoHttp.Segment(segment);
    }
    /// <summary>内容重定向只允许无用户信息的 HTTPS；首次之外全部移除 API Key，次数有界。</summary>
    private async Task<long> CopyCoreAsync(Uri initialUri, Stream destination, CancellationToken cancellationToken)
    {
        long copied = 0; var uri = initialUri;
        using (var client = factory.CreateClient(clientName))
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (hop == 0) request.Headers.Add("x-goog-api-key", key);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop >= redirects || response.Headers.Location is not { } location) throw new InvalidDataException("视频内容重定向缺少地址或超出显式次数上限。");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Scheme != "https" || next.UserInfo.Length != 0 || next.Fragment.Length != 0) throw new InvalidDataException("视频内容重定向必须是无凭据和片段的 HTTPS 地址。");
                    uri = next; continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var body = await HttpBodyReader.ReadAsync(response.Content, errorBytes, cancellationToken).ConfigureAwait(false);
                    throw new VideoUpstreamException("Gemini 视频内容调用失败。", response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
                }
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !(mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
                {
                    var body = await HttpBodyReader.ReadAsync(response.Content, errorBytes, cancellationToken).ConfigureAwait(false);
                    throw new VideoUpstreamException("Gemini 视频内容未返回二进制视频媒体。", response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
                }
                copied = await HttpBodyReader.CopyToAsync(response.Content, destination, maximumBytes, cancellationToken).ConfigureAwait(false); break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); return copied;
    }
}
