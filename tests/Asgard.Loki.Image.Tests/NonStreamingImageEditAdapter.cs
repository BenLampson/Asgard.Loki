namespace Asgard.Loki.Image.Tests;

/// <summary>普通编辑目标未声明原生流式，不能因筛选掉此目标而偷偷改变路由。</summary>
internal sealed class NonStreamingImageEditAdapter : IImageEditAdapter
{
    public string Id => "non-streaming";
    internal int Calls { get; private set; }
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    { Calls++; throw new InvalidOperationException("Must not dispatch streaming fallback"); }
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    { Calls++; throw new InvalidOperationException("Must not dispatch streaming fallback"); }
}
