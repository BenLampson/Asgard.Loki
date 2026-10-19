using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Asgard.Loki.Image.OpenAI.Internal;

namespace Asgard.Loki.Image.OpenAI;

/// <summary>OpenAI 原生 JSON/multipart 图像编辑目标，不与生成/其他能力共享调用状态或隐式重试。</summary>
public sealed class OpenAiImageEditAdapter : IStreamingImageEditAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiImageOptions _options;
    private readonly Uri _uri;
    /// <summary>独立声明完整编辑目标；复用图像配置类型，不隐式借用已注册生成目标。</summary>
    public OpenAiImageEditAdapter(IHttpClientFactory factory, OpenAiImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        _ = new UTF8Encoding(false, true).GetByteCount(options.Model);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0) throw new ArgumentException("编辑基础地址必须是无凭据/查询/片段的 HTTP(S) 绝对地址。");
        _factory = factory; _options = options; _uri = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "images/edits");
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageEditPayload.Snapshot(request, cancellationToken);
        return SendAsync(OpenAiImageEditPayload.Json(snapshot, _options.Model), snapshot.Model, cancellationToken);
    }
    /// <inheritdoc />
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageEditPayload.Snapshot(request, cancellationToken);
        return SendAsync(OpenAiImageEditPayload.Multipart(snapshot, _options.Model), snapshot.Model, cancellationToken);
    }
    /// <inheritdoc />
    public IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageEditPayload.Snapshot(request, cancellationToken, streaming: true);
        return SingleStream(() => OpenAiImageEditPayload.Json(snapshot, _options.Model), snapshot.Model, cancellationToken);
    }
    /// <inheritdoc />
    public IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageEditPayload.Snapshot(request, cancellationToken, streaming: true);
        return SingleStream(() => OpenAiImageEditPayload.Multipart(snapshot, _options.Model), snapshot.Model, cancellationToken);
    }
    /// <summary>每次流式调用只可枚举一次，不让复用枚举隐式再次读取借用上传。</summary>
    private IAsyncEnumerable<ImageEditEvent> SingleStream(Func<HttpContent> create, string publicModel, CancellationToken token)
    {
        var started = 0;
        return StreamPayloadAsync(() =>
        {
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("原生编辑流不能重复枚举或重放上传。");
            return create();
        }, publicModel, token);
    }
    /// <summary>原生完成项必须等待 EOF/长度和所有传输资源成功释放；不拼接图片或重新上传。</summary>
    private async IAsyncEnumerable<ImageEditEvent> StreamPayloadAsync(Func<HttpContent> create, string publicModel,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ImageEditEvent? terminal = null;
        using (var content = create())
        using (var message = CreateMessage(content))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        {
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var metadata = new ImageResultMetadata(publicModel, _options.Model, Id);
            if (!response.IsSuccessStatusCode || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                ImageResponseBody.RequireJson(response); var body = await ImageResponseBody.ReadAsync(response, token).ConfigureAwait(false);
                _ = OpenAiImageEditResponse.Result(response, body, metadata);
                throw new InvalidDataException("编辑流式入口未返回 SSE，不将普通结果改造成事件流。");
            }
            var type = response.Content.Headers.ContentType;
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("编辑 SSE 必须为原生 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("编辑 SSE Content-Length 无效。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                await foreach (var item in OpenAiImageEditSse.ReadAsync(source, expected, metadata, (int)response.StatusCode, type?.ToString(), token).ConfigureAwait(false))
                { if (item.IsCompleted) terminal = item; else yield return item; }
            }
        }
        token.ThrowIfCancellationRequested(); yield return terminal ?? throw new InvalidDataException("编辑流缺少原生完成项。");
    }
    /// <summary>一次请求，结果在所有响应/客户端资源成功释放后才返回。</summary>
    private async Task<ImageGenerationResult> SendAsync(HttpContent content, string publicModel, CancellationToken token)
    {
        ImageGenerationResult result;
        using (content)
        using (var message = CreateMessage(content))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            ImageResponseBody.RequireJson(response);
            var body = await ImageResponseBody.ReadAsync(response, token).ConfigureAwait(false);
            result = OpenAiImageEditResponse.Result(response, body, new(publicModel, _options.Model, Id));
        }
        token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>原生身份头来自固定配置，地址不受 JSON/表单扩展字段控制。</summary>
    private HttpRequestMessage CreateMessage(HttpContent content)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _uri);
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = content; return message;
        }
        catch { message.Dispose(); throw; }
    }
}
