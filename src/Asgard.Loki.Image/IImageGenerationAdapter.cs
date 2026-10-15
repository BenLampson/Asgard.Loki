namespace Asgard.Loki.Image;

/// <summary>图像供应商独立的生成适配契约。</summary>
public interface IImageGenerationAdapter
{
    /// <summary>供应商实例身份。</summary>
    string Id { get; }
    /// <summary>创建完整图像结果。</summary>
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);
    /// <summary>读取图像生成事件。</summary>
    IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);
}
