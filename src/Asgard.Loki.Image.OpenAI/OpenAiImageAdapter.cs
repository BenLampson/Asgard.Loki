using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Asgard.Loki.Image.OpenAI.Internal;

namespace Asgard.Loki.Image.OpenAI;

/// <summary>实现 OpenAI Images Generations 的完整结果与 SSE 部分图像流。</summary>
public sealed class OpenAiImageAdapter : IImageGenerationAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiImageOptions _options;

    /// <summary>校验图像供应商配置，不发起网络请求。</summary>
    public OpenAiImageAdapter(IHttpClientFactory factory, OpenAiImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        _ = new UTF8Encoding(false, true).GetByteCount(options.Model);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("https" or "http")
            || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0)
            throw new ArgumentException("图像基础地址必须是没有凭据、查询参数和片段的 HTTP(S) 绝对地址。", nameof(options));
        _factory = factory;
        _options = options;
    }
    /// <inheritdoc />
    public string Id => _options.Id;

    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        => SendAsync(OpenAiImageGenerationPayload.Snapshot(request, false, cancellationToken), cancellationToken);

    /// <summary>结果须等待正常正文结束和传输资源释放，不将 JSON 错误对象当作图片。</summary>
    private async Task<ImageGenerationResult> SendAsync(ImageGenerationRequest request, CancellationToken token)
    {
        ImageGenerationResult result;
        using (var content = OpenAiImageGenerationPayload.Create(request, _options.Model))
        using (var message = CreateMessage(content))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            ImageResponseBody.RequireJson(response); var body = await ImageResponseBody.ReadAsync(response, token).ConfigureAwait(false);
            result = Result(response, body, Metadata(request));
        }
        token.ThrowIfCancellationRequested(); return result;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageGenerationPayload.Snapshot(request, true, cancellationToken); var started = 0;
        return StreamPayloadAsync(snapshot, () =>
        {
            if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("原生图像生成流不能重复枚举或重放。");
        }, cancellationToken);
    }
    /// <summary>原始完成项保留到 EOF/长度及源、响应和客户端释放全部成功，不拼接部分图片。</summary>
    private async IAsyncEnumerable<ImageGenerationEvent> StreamPayloadAsync(ImageGenerationRequest request, Action start,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); ImageGenerationEvent? terminal = null;
        using (var content = OpenAiImageGenerationPayload.Create(request, _options.Model))
        using (var message = CreateMessage(content))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        {
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(type?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                ImageResponseBody.RequireJson(response); var body = await ImageResponseBody.ReadAsync(response, token).ConfigureAwait(false);
                _ = Result(response, body, Metadata(request)); throw new InvalidDataException("生成流未返回原生 SSE，不伪造事件流。");
            }
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("生成 SSE 必须为 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null) throw new InvalidDataException("生成 SSE Content-Length 无效。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                await foreach (var item in OpenAiImageGenerationSse.ReadAsync(source, expected, Metadata(request), (int)response.StatusCode, type?.ToString(), token).ConfigureAwait(false))
                { if (item.Type == "image_generation.completed") terminal = item; else yield return item; }
            }
        }
        token.ThrowIfCancellationRequested(); yield return terminal ?? throw new InvalidDataException("生成流没有原生完成项。");
    }
    /// <summary>只有实际目标配置能决定端点/身份；创建消息失败时释放自身资源。</summary>
    private HttpRequestMessage CreateMessage(HttpContent content)
    {
        var uri = new Uri(new Uri(_options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "images/generations");
        var message = new HttpRequestMessage(HttpMethod.Post, uri);
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = content; return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>将公共路由身份与上游模型分开返回。</summary>
    private ImageResultMetadata Metadata(ImageGenerationRequest request) => new(request.Model, _options.Model, Id);
    /// <summary>保留旧异常类型及真实状态，同时保留任意编码的原始失败字节。</summary>
    private static ImageGenerationResult Result(HttpResponseMessage response, byte[] body, ImageResultMetadata metadata)
        => ImageNativeJson.Result(response, body, metadata, () => new ImageUpstreamException(
            "OpenAI 图像生成返回原生失败。", (int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString()));
}
