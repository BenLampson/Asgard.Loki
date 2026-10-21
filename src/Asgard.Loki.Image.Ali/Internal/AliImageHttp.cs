using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Ali.Internal;

/// <summary>阿里图片包内的固定目标 HTTP 所有权与原生错误边界，无重试或目标切换。</summary>
internal sealed class AliImageHttp
{
    private readonly IHttpClientFactory _factory;
    internal AliImageOptions Options { get; }
    internal AliImageHttp(IHttpClientFactory factory, AliImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); AliImageJson.Text(text); }
        if (options.ApiKey.Any(value => value is < '!' or > '~')) throw new ArgumentException("阿里凭据必须为无空格/控制字符 ASCII 文本。");
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http") || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("阿里原生根地址必须为无凭据、查询、转义路径和片段的 HTTP(S) 地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; Options = options;
    }
    internal Uri Address(string relative) => new(new Uri(Options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), relative);
    internal ImageResultMetadata Metadata(string publicModel) => new(publicModel, Options.Model, Options.Id);
    internal HttpClient Client()
    {
        var client = _factory.CreateClient(Options.HttpClientName);
        try
        {
            client.DefaultRequestHeaders.Authorization = null;
            foreach (var header in new[] { "api-key", "X-DashScope-Async", "X-DashScope-SSE" }) client.DefaultRequestHeaders.Remove(header);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    internal HttpRequestMessage Message(Uri address, byte[]? bytes, bool asyncTask = false, bool streaming = false)
    {
        var message = new HttpRequestMessage(bytes is null ? HttpMethod.Get : HttpMethod.Post, address);
        try
        {
            message.Headers.Authorization = new("Bearer", Options.ApiKey); message.Headers.Accept.ParseAdd(streaming ? "text/event-stream" : "application/json");
            if (asyncTask) message.Headers.Add("X-DashScope-Async", "enable");
            if (streaming) message.Headers.Add("X-DashScope-SSE", "enable");
            if (bytes is not null) { message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json"); }
            return message;
        }
        catch { message.Dispose(); throw; }
    }
    internal static void Validate(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (response.RequestMessage is { } actual && (actual.Method != request.Method || actual.RequestUri != request.RequestUri))
            throw new InvalidDataException("阿里图片实际响应目标/方法与绑定不一致。");
    }
    internal async Task<JsonElement> SendAsync(Uri address, byte[]? body, bool asyncTask, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (var request = Message(address, body, asyncTask))
        using (var client = Client())
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); Validate(response, request);
            bytes = await HttpBodyReader.ReadAsync(response.Content, Options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested(); return Result(bytes, status, type);
    }
    internal static JsonElement Result(byte[] bytes, int status, string? type)
    {
        if (status is < 200 or >= 300) throw new ImageUpstreamException("阿里图片 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("阿里完整结果必须为 UTF-8 JSON。");
        var root = AliImageJson.Parse(bytes);
        if (AliImageJson.Error(root)) throw new ImageUpstreamException("阿里图片原生 API 错误。", status, bytes, type);
        return root;
    }
}
