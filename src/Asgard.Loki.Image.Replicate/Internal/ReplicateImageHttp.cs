using System.Net.Http.Headers;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Replicate.Internal;

/// <summary>本供应商的原生 POST/认证与完整结果边界，不轮询或下载返回引用。</summary>
internal sealed class ReplicateImageHttp
{
    private readonly IHttpClientFactory _factory;
    private readonly Uri _root;
    internal ReplicateImageOptions Options { get; }
    internal Uri Predictions { get; }
    internal ReplicateImageHttp(IHttpClientFactory factory, ReplicateImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); ReplicateImageJson.Text(text); }
        if (options.ApiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("Replicate 凭据必须是无空白和控制字符的 ASCII 文本。");
        var parts = options.Model.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            throw new ArgumentException("Replicate 模型必须为明确的 owner/name 资源。");
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%')) throw new ArgumentException("Replicate 根地址必须是无凭据、查询、转义路径和片段的 HTTPS 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0 || options.MaxUploadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; Options = options; _root = new(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/");
        Predictions = new(_root, "models/" + Uri.EscapeDataString(parts[0]) + "/" + Uri.EscapeDataString(parts[1]) + "/predictions");
    }
    /// <summary>完整读取并结束本次 HTTP 所有权之后返回原始字节、状态及内容类型。</summary>
    internal async Task<(byte[] Bytes, int Status, string? Type)> PostAsync(Uri address, HttpContent content, bool wait, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (content)
        using (var message = new HttpRequestMessage(HttpMethod.Post, address) { Content = content })
        {
            message.Headers.Authorization = new("Bearer", Options.ApiKey); message.Headers.Accept.ParseAdd("application/json");
            if (wait) message.Headers.Add("Prefer", "wait");
            using var client = _factory.CreateClient(Options.HttpClientName);
            foreach (var name in new[] { "Authorization", "api-key", "x-api-key", "Prefer" }) client.DefaultRequestHeaders.Remove(name);
            token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != address)) throw new InvalidDataException("Replicate 图片实际响应目标/方法与固定调用不符。");
            bytes = await HttpBodyReader.ReadAsync(response.Content, Options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested();
        if (status is < 200 or >= 300) throw new ImageUpstreamException("Replicate 图片 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Replicate 图片原生结果必须为 UTF-8 JSON。");
        return (bytes, status, type);
    }
    /// <summary>一次模型图像输入上传，调用者保留源流所有权，不借用返回 URL 执行其他请求。</summary>
    internal Task<(byte[] Bytes, int Status, string? Type)> UploadAsync(ImageUpload upload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(upload); ArgumentException.ThrowIfNullOrWhiteSpace(upload.FileName);
        ReplicateImageJson.Text(upload.FileName);
        if (upload.FileName.Any(char.IsControl) || upload.FileName.Contains('"') || System.Text.Encoding.UTF8.GetByteCount(upload.FileName) > 255)
            throw new ArgumentException("Replicate 输入文件名不能包含控制字符/引号或超过 255 UTF-8 字节。");
        var type = upload.ContentType is null ? new MediaTypeHeaderValue("application/octet-stream") : MediaTypeHeaderValue.Parse(upload.ContentType);
        var content = new MultipartFormDataContent();
        try
        {
            var part = new BorrowedStreamContent(upload.Content, Options.MaxUploadBytes); content.Add(part, "content", upload.FileName); part.Headers.ContentType = type;
            return PostAsync(new(_root, "files"), content, false, token);
        }
        catch { content.Dispose(); throw; }
    }
}
