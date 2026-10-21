namespace Asgard.Loki.Image.Tests;

/// <summary>源在 MoveNext 正常返回结束后从 DisposeAsync 抛错，用于验证真实的释放窗口。</summary>
internal sealed class DisposalFaultImageEditAdapter : IStreamingImageEditAdapter, IAsyncEnumerable<ImageEditEvent>, IAsyncEnumerator<ImageEditEvent>
{
    private int _index = -1;
    public string Id => "dispose-fault";
    public ImageEditEvent Current => ImageEditFixture.Event(_index == 1);
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken cancellationToken = default) => this;
    public IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default) => this;
    public IAsyncEnumerator<ImageEditEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_index < 2);
    public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("fixture disposal failed"));
}
