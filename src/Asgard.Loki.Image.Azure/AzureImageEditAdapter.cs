using Asgard.Loki.Image.Azure.Internal;
using System.Runtime.CompilerServices;

namespace Asgard.Loki.Image.Azure;

/// <summary>单次 Azure 原生 multipart 编辑，借用图片/遮罩，不从 JSON 引用下载文件。</summary>
public sealed class AzureImageEditAdapter : IStreamingImageEditAdapter
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
    /// <summary>日期版本不把 JSON 图片引用变成下载上传，流式同样明确拒绝。</summary>
    public IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); throw new NotSupportedException("此 Azure 日期版本目标只提供原生 multipart 编辑。"); }
    /// <summary>保留借用输入的原始位置/身份，参数在枚举前冻结，内容仅允许单次发送。</summary>
    public IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    {
        var copy = AzureImagePayload.Snapshot(request, _target.Options, cancellationToken, true); var expected = AzureImagePayload.ExpectedImages(copy); var started = 0;
        return StreamCoreAsync(copy, expected, () =>
        { if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Azure 原生编辑流不能重放。"); }, cancellationToken);
    }
    /// <summary>单个原生图片完成不代表整个多图调用完成；原 JSON/SSE 顺序与关联信息保持不变。</summary>
    private async IAsyncEnumerable<ImageEditEvent> StreamCoreAsync(ImageMultipartEditRequest request, int expectedImages, Action start,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); start(); var metadata = new ImageResultMetadata(request.Model, _target.Options.Deployment, Id);
        await foreach (var frame in AzureImageStream.ReadAsync(_target, "edits", () => AzureImagePayload.Multipart(request, _target.Options), expectedImages, metadata,
            (response, item) => new ImageEditUpstreamException("Azure 原生图像编辑流失败。", (int)response.Status, response.Body, response.ContentType)
            { EditEvent = item is null ? null : new(item.Data, false, metadata, item.EventName, item.EventId, item.RetryMilliseconds) }, token).ConfigureAwait(false))
            yield return new(frame.Data, frame.IsCompleted, metadata, frame.EventName, frame.EventId, frame.RetryMilliseconds);
    }
}
