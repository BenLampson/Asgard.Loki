namespace Asgard.Loki.Image;

/// <summary>完整图像编辑目标，原生 JSON 与 multipart 调用各自显式执行，不退化成文本生成。</summary>
public interface IImageEditAdapter
{
    /// <summary>固定目标实例身份，包括其实际地址、模型、凭据与实现的绑定。</summary>
    string Id { get; }
    /// <summary>单次 JSON 编辑；供应商保留全部原生结果和未知字段。</summary>
    Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default);
    /// <summary>单次 multipart 编辑；上传为借用资源，不重放或自动切换目标。</summary>
    Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default);
}
