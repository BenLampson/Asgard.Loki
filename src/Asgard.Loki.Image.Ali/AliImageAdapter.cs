using System.Runtime.CompilerServices;
using Asgard.Loki.Image.Ali.Internal;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Ali;

/// <summary>固定阿里原生同步/图文流目标，不把同步调用包装为虚构异步任务。</summary>
public sealed class AliImageAdapter : IImageGenerationAdapter
{
    private readonly AliImageHttp _http;
    /// <summary>绑定独立图片根地址、模型与凭据，借用工厂。</summary>
    public AliImageAdapter(IHttpClientFactory factory, AliImageOptions options) => _http = new(factory, options);
    /// <inheritdoc />
    public string Id => _http.Options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return GenerateNativeAsync(AliImagePayload.Bridge(request), cancellationToken); }
    /// <summary>完整原生 input/messages、parameters 和扩展字段，直接调用当前目标。</summary>
    public Task<ImageGenerationResult> GenerateNativeAsync(AliNativeImageRequest request, CancellationToken cancellationToken = default)
        => GenerateCoreAsync(AliImagePayload.Prepare(request, _http.Options, false, cancellationToken), request.PublicModel, cancellationToken);
    private async Task<ImageGenerationResult> GenerateCoreAsync(byte[] body, string publicModel, CancellationToken token)
    {
        var response = await _http.SendRawAsync(_http.Address("services/aigc/multimodal-generation/generation"), body, false, token).ConfigureAwait(false);
        var root = response.Data;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != System.Text.Json.JsonValueKind.Object
            || !output.TryGetProperty("choices", out var choices) || choices.ValueKind != System.Text.Json.JsonValueKind.Array || choices.GetArrayLength() == 0
            || output.TryGetProperty("finished", out var finished) && finished.ValueKind != System.Text.Json.JsonValueKind.True)
            throw new InvalidDataException("阿里同步结果缺少完整原生 output/choices。");
        foreach (var choice in choices.EnumerateArray())
            if (choice.ValueKind != System.Text.Json.JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var reason)
                || reason.ValueKind != System.Text.Json.JsonValueKind.String || reason.GetString() != "stop") throw new InvalidDataException("阿里同步 choice 未正常 stop。");
        var images = AliImageJson.Images(root); token.ThrowIfCancellationRequested(); return new(root, _http.Metadata(publicModel), images) { RawResponse = response.Raw };
    }
    /// <inheritdoc />
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return StreamNativeAsync(AliImagePayload.Bridge(request), cancellationToken); }
    /// <summary>显式原生图文流；要求 enable_interleave=true，仅补入缺省 stream=true，枚举前冻结请求。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamNativeAsync(AliNativeImageRequest request, CancellationToken cancellationToken = default)
    {
        var body = AliImagePayload.Prepare(request, _http.Options, true, cancellationToken); var model = request.PublicModel; var started = 0;
        return StreamCoreAsync(body, model, () => { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("阿里图片流不能重放。"); }, cancellationToken);
    }
    private async IAsyncEnumerable<ImageGenerationEvent> StreamCoreAsync(byte[] body, string model, Action start, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); var tail = new List<ImageGenerationEvent>();
        using (var message = _http.Message(_http.Address("services/aigc/multimodal-generation/generation"), body, streaming: true))
        using (var client = _http.Client())
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); AliImageHttp.Validate(response, message);
            var type = response.Content.Headers.ContentType;
            if (!response.IsSuccessStatusCode || !string.Equals(type?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await HttpBodyReader.ReadAsync(response.Content, _http.Options.MaxResponseBytes, token).ConfigureAwait(false);
                _ = AliImageHttp.Result(bytes, (int)response.StatusCode, type?.ToString()); throw new InvalidDataException("阿里流未返回 SSE，不从 JSON 造事件。");
            }
            if (type?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("阿里 SSE 必须为 UTF-8。");
            var expected = response.Content.Headers.ContentLength;
            if (response.Content.Headers.Contains("Content-Length") && expected is null || expected < 0 || expected > _http.Options.MaxResponseBytes) throw new InvalidDataException("阿里 SSE 声明长度无效或超限。");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                using var bounded = new AliImageReadStream(source, expected, _http.Options.MaxResponseBytes);
                await foreach (var item in AliImageSse.ReadAsync(bounded, _http.Metadata(model), (int)response.StatusCode, type?.ToString(), token).ConfigureAwait(false))
                { if (tail.Count > 0 || AliImageSse.HasStop(item)) tail.Add(item); else yield return item; }
            }
        }
        token.ThrowIfCancellationRequested(); foreach (var item in tail) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
