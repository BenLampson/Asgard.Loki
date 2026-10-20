using Asgard.Loki.Image.Azure.Internal;

namespace Asgard.Loki.Image.Azure;

/// <summary>单次 Azure 原生 multipart 编辑，借用图片/遮罩，不从 JSON 引用下载文件。</summary>
public sealed class AzureImageEditAdapter : IImageEditAdapter
{
    private readonly AzureImageTarget _target;
    /// <summary>编辑拥有独立完整目标，不借用生成路由、LLM 或音频配置。</summary>
    public AzureImageEditAdapter(IHttpClientFactory factory, AzureImageOptions options) => _target = new(factory, options);
    /// <inheritdoc />
    public string Id => _target.Options.Id;
    /// <summary>日期版本的原生编辑使用 multipart；不将 OpenAI JSON 引用转换为下载/上传。</summary>
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); throw new NotSupportedException("此 Azure 日期版本目标只提供原生 multipart 编辑。"); }
    /// <inheritdoc />
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    {
        var copy = AzureImagePayload.Snapshot(request, _target.Options, cancellationToken);
        var content = AzureImagePayload.Multipart(copy, _target.Options); return SendAsync(copy.Model, content, cancellationToken);
    }
    /// <summary>原生错误完整保留，读取与传输释放失败不能先发布成功。</summary>
    private async Task<ImageGenerationResult> SendAsync(string model, HttpContent content, CancellationToken token)
    {
        var response = await _target.SendAsync("edits", content, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        var result = AzureImageJson.Result(response, new(model, _target.Options.Deployment, Id), () => new ImageEditUpstreamException(
            "Azure 图像编辑返回原生失败。", (int)response.Status, response.Body, response.ContentType));
        token.ThrowIfCancellationRequested(); return result;
    }
}
