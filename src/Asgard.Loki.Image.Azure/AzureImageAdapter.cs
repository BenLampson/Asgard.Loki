using Asgard.Loki.Image.Azure.Internal;

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
        var content = AzureImagePayload.Generation(request, _target.Options, cancellationToken);
        return SendAsync(request.Model, content, cancellationToken);
    }
    /// <summary>完整原生结果在 EOF、响应与客户端释放后交付，未知字段及原生用量不计算/改写。</summary>
    private async Task<ImageGenerationResult> SendAsync(string model, HttpContent content, CancellationToken token)
    {
        var response = await _target.SendAsync("generations", content, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        var result = AzureImageJson.Result(response, new(model, _target.Options.Deployment, Id), () => new ImageUpstreamException(
            "Azure 图像生成返回原生失败。", (int)response.Status, response.Body, response.ContentType));
        token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>原生 SSE 将独立实现，不把完整 JSON 包装成假事件。</summary>
    public IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); throw new NotSupportedException("Azure 原生图像 SSE 尚未实现。"); }
}
