using System.Runtime.CompilerServices;
using Asgard.Loki.Image.GoogleVertex.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.GoogleVertex;

/// <summary>固定 Vertex 原生图片目标，完整资源与认证由构造入口显式选择。</summary>
public sealed class GoogleVertexImageAdapter : IImageGenerationAdapter
{
    private readonly VertexImageHttp _http;
    /// <summary>明确选择 API Key 的项目资源或 Express 目标，不借用云端令牌源。</summary>
    public GoogleVertexImageAdapter(IHttpClientFactory factory, GoogleVertexImageOptions options) => _http = new(factory, options);
    /// <summary>明确选择完整项目资源和借用令牌源；主体、来源身份和有效期始终复核。</summary>
    public GoogleVertexImageAdapter(IHttpClientFactory factory, IGoogleVertexImageTokenSource tokenSource, GoogleVertexImageOptions options, TimeProvider? timeProvider = null)
    { ArgumentNullException.ThrowIfNull(tokenSource); _http = new(factory, options, tokenSource, timeProvider); }
    /// <inheritdoc />
    public string Id => _http.Options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return GenerateNativeAsync(VertexImagePayload.Bridge(request), cancellationToken); }
    /// <summary>完整原生 contents、generationConfig、签名和扩展字段，直接调用当前目标。</summary>
    public Task<ImageGenerationResult> GenerateNativeAsync(GoogleVertexNativeImageRequest request, CancellationToken cancellationToken = default)
        => GenerateCoreAsync(VertexImagePayload.Prepare(request, _http.Options, cancellationToken), request.PublicModel, cancellationToken);
    private async Task<ImageGenerationResult> GenerateCoreAsync(byte[] body, string publicModel, CancellationToken token)
    {
        var root = await _http.SendAsync(body, token).ConfigureAwait(false);
        var lifecycle = new VertexImageLifecycle(); lifecycle.Observe(root); lifecycle.Complete();
        var images = VertexImageJson.Images(root); _http.Check(token); return new(root, _http.Metadata(publicModel), images);
    }
    /// <inheritdoc />
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return StreamNativeAsync(VertexImagePayload.Bridge(request), cancellationToken); }
    /// <summary>显式原生 SSE；正文不添加 stream 或改写模态，枚举前冻结请求。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamNativeAsync(GoogleVertexNativeImageRequest request, CancellationToken cancellationToken = default)
    {
        var body = VertexImagePayload.Prepare(request, _http.Options, cancellationToken); var model = request.PublicModel; var started = 0;
        return StreamCoreAsync(body, model, () => { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Gemini图片流不能重放。"); }, cancellationToken);
    }
    private async IAsyncEnumerable<ImageGenerationEvent> StreamCoreAsync(byte[] body, string model, Action start, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); var access = await _http.AuthenticateAsync(token).ConfigureAwait(false); var tail = new List<ImageGenerationEvent>();
        using (var message = _http.Message(body, true, access))
        using (var client = _http.Client(token, access))
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); VertexImageHttp.Validate(response, message);
            var type = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(type?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await HttpBodyReader.ReadAsync(response.Content, _http.Options.MaxResponseBytes, token).ConfigureAwait(false);
                _ = VertexImageHttp.Result(bytes, (int)response.StatusCode, type?.ToString()); throw new InvalidDataException("Gemini流未返回 SSE，不从 JSON 造事件。");
            }
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Gemini SSE 必须为 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null || expected < 0 || expected > _http.Options.MaxResponseBytes) throw new InvalidDataException("Gemini SSE 声明长度无效或超限。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                using var bounded = new VertexImageReadStream(source, expected, _http.Options.MaxResponseBytes);
                await foreach (var item in VertexImageSse.ReadAsync(bounded, _http.Metadata(model), (int)response.StatusCode, type?.ToString(), token).ConfigureAwait(false))
                { _http.Check(token); if (tail.Count > 0 || VertexImageSse.IsTerminal(item)) tail.Add(item); else yield return item; }
            }
        }
        _http.Check(token); foreach (var item in tail) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
