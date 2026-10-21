namespace Asgard.Loki.Image;

/// <summary>完整目标显式提供原生图像编辑事件流能力，不从普通结果模拟事件。</summary>
public interface IStreamingImageEditAdapter : IImageEditAdapter
{
    /// <summary>原生 JSON 请求的完整编辑事件流。</summary>
    IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken cancellationToken = default);
    /// <summary>原生 multipart 请求的完整编辑事件流，借用上传必须保持有效。</summary>
    IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default);
}
