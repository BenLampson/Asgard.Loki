using Asgard.Loki.Image.Azure.Internal;
using System.Runtime.CompilerServices;

namespace Asgard.Loki.Image.Azure;

/// <summary>单次 Azure 原生 JSON 图像生成，保留完整结果及实际部署身份。</summary>
public sealed class AzureImageAdapter : IImageGenerationAdapter
{
    private readonly AzureImageTarget _target;
    /// <summary>验证完整目标并借用工厂，不读取配置文件或调用上游。</summary>
    public AzureImageAdapter(IHttpClientFactory factory, AzureImageOptions options) => _target = new(factory, options);
    /// <inheritdoc />
    public string Id => _target.Options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var payload = AzureImagePayload.Generation(request, _target.Options, cancellationToken);
        return SendAsync(payload.Model, AzureImagePayload.Json(payload.Body), cancellationToken);
    }
    /// <summary>完整原生结果在 EOF、响应与客户端释放后交付，未知字段及原生用量不计算/改写。</summary>
    private async Task<ImageGenerationResult> SendAsync(string model, HttpContent content, CancellationToken token)
    {
        var response = await _target.SendAsync("generations", content, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        var result = AzureImageJson.Result(response, new(model, _target.Options.Deployment, Id), () => new ImageUpstreamException(
            "Azure 图像生成返回原生失败。", (int)response.Status, response.Body, response.ContentType));
        token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>在调用时冻结原生字段，显式流请求不覆盖 false、不把完整 JSON 包装成假事件。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var payload = AzureImagePayload.Generation(request, _target.Options, cancellationToken, true); var started = 0;
        return StreamCoreAsync(payload, () =>
        { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Azure 原生图像流不能重放。"); }, cancellationToken);
    }
    /// <summary>按原始顺序保持全部原生图片事件，完成项必须等待 EOF 与传输资源释放。</summary>
    private async IAsyncEnumerable<ImageGenerationEvent> StreamCoreAsync(AzureImageGenerationPayload payload, Action start,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); var metadata = new ImageResultMetadata(payload.Model, _target.Options.Deployment, Id);
        await foreach (var frame in AzureImageStream.ReadAsync(_target, "generations", () => AzureImagePayload.Json(payload.Body), payload.ExpectedImages, metadata,
            (response, item) => new ImageUpstreamException("Azure 原生图像生成流失败。", (int)response.Status, response.Body, response.ContentType)
            { GenerationEvent = item is null ? null : new(item.Data, metadata, item.EventName, item.EventId, item.RetryMilliseconds) }, token).ConfigureAwait(false))
            yield return new(frame.Data, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
    }
}
