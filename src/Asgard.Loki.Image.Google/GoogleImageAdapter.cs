using System.Runtime.CompilerServices;
using Asgard.Loki.Image.Google.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Google;

/// <summary>固定Gemini原生同步/图文流目标，不把同步调用包装为虚构异步任务。</summary>
public sealed class GoogleImageAdapter : IImageGenerationAdapter
{
    private readonly GoogleImageHttp _http;
    /// <summary>绑定独立图片根地址、模型与凭据，借用工厂。</summary>
    public GoogleImageAdapter(IHttpClientFactory factory, GoogleImageOptions options) => _http = new(factory, options);
    /// <inheritdoc />
    public string Id => _http.Options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return GenerateNativeAsync(GoogleImagePayload.Bridge(request), cancellationToken); }
    /// <summary>完整原生 contents、generationConfig、签名和扩展字段，直接调用当前目标。</summary>
    public Task<ImageGenerationResult> GenerateNativeAsync(GoogleNativeImageRequest request, CancellationToken cancellationToken = default)
        => GenerateCoreAsync(GoogleImagePayload.Prepare(request, _http.Options, cancellationToken), request.PublicModel, cancellationToken);
    private async Task<ImageGenerationResult> GenerateCoreAsync(byte[] body, string publicModel, CancellationToken token)
    {
        var response = await _http.SendRawAsync(Address(false), body, token).ConfigureAwait(false);
        var root = response.Data; var lifecycle = new GoogleImageLifecycle(); lifecycle.Observe(root); lifecycle.Complete();
        var images = GoogleImageJson.Images(root); token.ThrowIfCancellationRequested(); return new(root, _http.Metadata(publicModel), images) { RawResponse = response.Raw };
    }
    private Uri Address(bool streaming) => _http.Address("models/" + Uri.EscapeDataString(_http.Options.Model)
        + (streaming ? ":streamGenerateContent?alt=sse" : ":generateContent"));
    /// <inheritdoc />
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return StreamNativeAsync(GoogleImagePayload.Bridge(request), cancellationToken); }
    /// <summary>显式原生 SSE；正文不添加 stream 或改写模态，枚举前冻结请求。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamNativeAsync(GoogleNativeImageRequest request, CancellationToken cancellationToken = default)
    {
        var body = GoogleImagePayload.Prepare(request, _http.Options, cancellationToken); var model = request.PublicModel; var started = 0;
        return StreamCoreAsync(body, model, () => { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Gemini图片流不能重放。"); }, cancellationToken);
    }
    private async IAsyncEnumerable<ImageGenerationEvent> StreamCoreAsync(byte[] body, string model, Action start, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); var tail = new List<ImageGenerationEvent>();
        using (var message = _http.Message(Address(true), body, streaming: true))
        using (var client = _http.Client())
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); GoogleImageHttp.Validate(response, message);
            var type = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(type?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await HttpBodyReader.ReadAsync(response.Content, _http.Options.MaxResponseBytes, token).ConfigureAwait(false);
                _ = GoogleImageHttp.Result(bytes, (int)response.StatusCode, type?.ToString()); throw new InvalidDataException("Gemini流未返回 SSE，不从 JSON 造事件。");
            }
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Gemini SSE 必须为 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null || expected < 0 || expected > _http.Options.MaxResponseBytes) throw new InvalidDataException("Gemini SSE 声明长度无效或超限。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                using var bounded = new GoogleImageReadStream(source, expected, _http.Options.MaxResponseBytes);
                await foreach (var item in GoogleImageSse.ReadAsync(bounded, _http.Metadata(model), (int)response.StatusCode, type?.ToString(), token).ConfigureAwait(false))
                { if (tail.Count > 0 || GoogleImageSse.IsTerminal(item)) tail.Add(item); else yield return item; }
            }
        }
        token.ThrowIfCancellationRequested(); foreach (var item in tail) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
