using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Asgard.Loki.Image.Volcengine.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Volcengine;

/// <summary>固定方舟图像原生 JSON/SSE 目标，支持同一接口的图像输入与无提示词图层拆分。</summary>
public sealed class VolcengineImageAdapter : IImageGenerationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly VolcengineImageOptions _options;
    private readonly Uri _address;
    /// <summary>校验完整目标并借用 HTTP 工厂，不读取其他能力的模型、凭据或状态。</summary>
    public VolcengineImageAdapter(IHttpClientFactory factory, VolcengineImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Model, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); ArkImageJson.Text(text); }
        if (options.ApiKey.Any(value => value is < '!' or > '~')) throw new ArgumentException("方舟凭据必须为无空格/控制字符的 ASCII 文本。");
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http") || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("方舟图片根地址必须是无凭据、查询、转义路径和片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; _options = options; _address = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "images/generations");
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return GenerateNativeAsync(ArkImagePayload.Bridge(request), cancellationToken); }
    /// <summary>直接在本固定供应商目标调用原生 JSON；不执行公共路由调度，允许合法省略的原生 prompt。</summary>
    public Task<ImageGenerationResult> GenerateNativeAsync(VolcengineNativeImageRequest request, CancellationToken cancellationToken = default)
        => SendAsync(ArkImagePayload.Prepare(request, _options, false, cancellationToken), cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return StreamNativeAsync(ArkImagePayload.Bridge(request), cancellationToken); }
    /// <summary>直接在本完整目标执行显式原生流，字段在枚举前冻结，不进行重连或自动重试。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamNativeAsync(VolcengineNativeImageRequest request, CancellationToken cancellationToken = default)
    {
        var payload = ArkImagePayload.Prepare(request, _options, true, cancellationToken); var started = 0;
        return StreamCoreAsync(payload, () => { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("方舟图像流不能重放。"); }, cancellationToken);
    }
    /// <summary>完整响应先结束 HTTP 所有权窗口，再解释原生整体错误与图片级结果。</summary>
    private async Task<ImageGenerationResult> SendAsync(ArkImageRequest request, CancellationToken token)
    {
        byte[] bytes; int status; string? type;
        using (var message = Message(request.Body, false))
        using (var client = Client())
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); Validate(response);
            bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false); status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        token.ThrowIfCancellationRequested(); var result = Result(bytes, status, type, Metadata(request)); token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>保留组图部分失败，汇总不伪装成图片；单图可按原生结果+EOF 结束且不造 completed。</summary>
    private async IAsyncEnumerable<ImageGenerationEvent> StreamCoreAsync(ArkImageRequest request, Action start, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); var metadata = Metadata(request); var tail = new List<ArkImageFrame>();
        using (var message = Message(request.Body, true))
        using (var client = Client())
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); Validate(response);
            var type = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(type?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await HttpBodyReader.ReadAsync(response.Content, _options.MaxResponseBytes, token).ConfigureAwait(false);
                _ = Result(bytes, (int)response.StatusCode, type?.ToString(), metadata);
                throw new InvalidDataException("方舟原生图片流没有返回 SSE，不从 JSON 造事件。", new ImageUpstreamException("方舟图片流响应协议不符。", (int)response.StatusCode, bytes, type?.ToString()));
            }
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("方舟图片 SSE 必须为 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null || expected < 0 || expected > _options.MaxResponseBytes) throw new InvalidDataException("方舟图片 SSE 声明长度无效或超限。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                using var counted = new ArkImageReadStream(source, expected, _options.MaxResponseBytes);
                await foreach (var frame in ArkImageSse.ReadAsync(counted, request.Grouped, item => new ImageUpstreamException("方舟图片原生流整体失败。", (int)response.StatusCode,
                    System.Text.Encoding.UTF8.GetBytes(item.RawData), type?.ToString()) { GenerationEvent = Event(item, metadata) }, token).ConfigureAwait(false))
                { if (frame.Deferred || tail.Count > 0) tail.Add(frame); else yield return Event(frame, metadata); }
            }
        }
        token.ThrowIfCancellationRequested(); foreach (var frame in tail) { token.ThrowIfCancellationRequested(); yield return Event(frame, metadata); }
    }
    /// <summary>错误是原生 HTTP/整体 error 才抛出；data[].error 保留在完整结果中，不丢掉其他图片。</summary>
    private static ImageGenerationResult Result(byte[] bytes, int status, string? contentType, ImageResultMetadata metadata)
    {
        if (status is < 200 or >= 300) throw new ImageUpstreamException("方舟图片 HTTP 调用失败。", status, bytes, contentType);
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("方舟完整图片结果必须为 UTF-8 JSON。");
        var root = ArkImageJson.Parse(bytes);
        if (root.TryGetProperty("error", out var error) && error.ValueKind != System.Text.Json.JsonValueKind.Null)
            throw new ImageUpstreamException("方舟图片返回原生整体错误。", status, bytes, contentType);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array) throw new InvalidDataException("方舟图片结果缺少原生 data 数组。");
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("方舟图片结果项必须为对象。");
            if (item.TryGetProperty("error", out var itemError) && itemError.ValueKind != System.Text.Json.JsonValueKind.Null) ArkImageJson.Error(itemError);
            else ArkImageJson.Image(item);
        }
        return new(root, metadata) { RawResponse = new(status, bytes, contentType) };
    }
    private ImageResultMetadata Metadata(ArkImageRequest request) => new(request.PublicModel, _options.Model, Id);
    private static ImageGenerationEvent Event(ArkImageFrame frame, ImageResultMetadata metadata) => new(frame.Data, metadata, frame.EventName, frame.EventId, frame.Retry);
    /// <summary>每次请求独立拥有内容；绑定模型/凭据由目标配置决定，不借用其他供应商头部。</summary>
    private HttpRequestMessage Message(byte[] body, bool streaming)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _address);
        try
        {
            message.Headers.Authorization = new("Bearer", _options.ApiKey); message.Headers.Accept.ParseAdd(streaming ? "text/event-stream" : "application/json");
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); return message;
        }
        catch { message.Dispose(); throw; }
    }
    private HttpClient Client()
    {
        var client = _factory.CreateClient(_options.HttpClientName);
        try { client.DefaultRequestHeaders.Authorization = null; client.DefaultRequestHeaders.Remove("api-key"); return client; }
        catch { client.Dispose(); throw; }
    }
    private void Validate(HttpResponseMessage response)
    {
        if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != _address)) throw new InvalidDataException("方舟图片实际响应目标/方法与固定目标不一致。");
    }
}
