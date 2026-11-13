using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image;

/// <summary>图片结果的有界二进制获取，独立于生成请求，不转发生成认证头。</summary>
internal sealed class LokiImageContentReader(IHttpClientFactory factory, LokiImageContentOptions options)
{
    internal async Task<LokiImageContent> ReadAsync(LokiGeneratedImage image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(image);
        if (image.Error is not null) throw new InvalidOperationException("原生图片条目包含错误，不能作为成功图片获取。");
        if (image.NativeImage.Content is { } binary)
        {
            if (binary.Bytes.Length > options.MaximumBytes) throw new InvalidDataException("图片二进制超过字节上限。");
            token.ThrowIfCancellationRequested(); return binary;
        }
        if (image.Base64 is { Length: > 0 } encoded)
        {
            string? declared = image.ContentType;
            if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = encoded.IndexOf(',');
                if (separator < 0 || !encoded[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("图片 data URI 必须为显式 Base64。");
                var uriType = encoded[5..(separator - 7)]; encoded = encoded[(separator + 1)..];
                if (declared is not null && !declared.Equals(uriType, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("图片原生媒体类型与 data URI 冲突。");
                declared = uriType;
            }
            if (declared is not null && (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(declared, out var type)
                || type.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true))
                throw new InvalidDataException("图片原生媒体类型无效。");
            var count = encoded.Count(character => !char.IsWhiteSpace(character));
            if ((long)count / 4 * 3 > (long)options.MaximumBytes + 2) throw new InvalidDataException("图片 Base64 超过字节上限。");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException error) { throw new InvalidDataException("图片 Base64 无效。", error); }
            if (bytes.Length == 0 || bytes.Length > options.MaximumBytes) throw new InvalidDataException("图片 Base64 解码内容为空或超限。");
            token.ThrowIfCancellationRequested(); return new(bytes, declared ?? Detect(bytes));
        }
        if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var address)
            || address.Scheme is not ("https" or "http") || address.UserInfo.Length != 0 || address.Fragment.Length != 0)
            throw new InvalidDataException("图片缺少可用的 Base64、二进制或 HTTP(S) URL。");
        byte[] body; string? contentType;
        using (var request = new HttpRequestMessage(HttpMethod.Get, address))
        using (var client = factory.CreateClient(options.HttpClientName))
        {
            // 使用独立内容客户端，绝不借用或复制生成目标的 Key 和组织等头部。
            client.DefaultRequestHeaders.Clear(); request.Headers.Accept.ParseAdd("image/*");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.RequestUri != address || actual.Method != HttpMethod.Get))
                throw new InvalidDataException("图片下载实际地址与请求不一致；内容客户端必须禁用自动重定向。");
            response.EnsureSuccessStatusCode();
            body = await HttpBodyReader.ReadAsync(response.Content, options.MaximumBytes, token).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            contentType = mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                ? mediaType : Detect(body);
            if (body.Length == 0 || contentType is null) throw new InvalidDataException("图片下载未返回可识别的图片内容。");
        }
        token.ThrowIfCancellationRequested(); return new(body, contentType);
    }

    /// <summary>只识别明确文件签名，不根据 URL 后缀或请求格式猜测。</summary>
    private static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        if (bytes.StartsWith(new byte[] { 73, 73, 42, 0 }) || bytes.StartsWith(new byte[] { 77, 77, 0, 42 })) return "image/tiff";
        return null;
    }
}
