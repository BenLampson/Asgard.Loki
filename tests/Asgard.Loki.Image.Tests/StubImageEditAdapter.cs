using System.Runtime.CompilerServices;

namespace Asgard.Loki.Image.Tests;

/// <summary>应用持有的图像生成/编辑替身，仅验证能力契约，不冒充 OpenAI HTTP 或真实图像。</summary>
internal sealed class StubImageEditAdapter(string id = "edit-one") : IStreamingImageEditAdapter, IImageGenerationAdapter, IDisposable
{
    public string Id { get; init; } = id;
    internal int Edits;
    internal int Multiparts;
    internal int Streams;
    internal int Generations;
    internal ImageEditRequest? LastJson { get; private set; }
    internal ImageMultipartEditRequest? LastMultipart { get; private set; }
    internal Action<ImageEditRequest>? OnJson { get; init; }
    internal Action<ImageMultipartEditRequest>? OnMultipart { get; init; }
    internal IReadOnlyList<ImageEditEvent> Events { get; init; } = [ImageEditFixture.Event(false), ImageEditFixture.Event(true)];
    internal Exception? Failure { get; init; }
    internal Exception? TailFailure { get; init; }
    internal Action? OnTail { get; init; }
    internal bool IteratorDisposed { get; private set; }
    internal bool Disposed { get; private set; }
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Edits); Capture(request); return Result(request.Model); }
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Multiparts); Capture(request); return Result(request.Model); }
    public IAsyncEnumerable<ImageEditEvent> StreamEditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Streams); Capture(request); return ReadAsync(cancellationToken); }
    public IAsyncEnumerable<ImageEditEvent> StreamMultipartEditAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Streams); Capture(request); return ReadAsync(cancellationToken); }
    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Generations); return Result(request.Model); }
    public async IAsyncEnumerable<ImageGenerationEvent> StreamAsync(ImageGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); yield break; }
    private void Capture(ImageEditRequest request) { LastJson = request; OnJson?.Invoke(request); }
    private void Capture(ImageMultipartEditRequest request) { LastMultipart = request; OnMultipart?.Invoke(request); }
    private Task<ImageGenerationResult> Result(string model) => Failure is not null ? Task.FromException<ImageGenerationResult>(Failure)
        : Task.FromResult(new ImageGenerationResult(ImageEditFixture.Golden("edit-native-response"), new(model, "native-image-model", Id)));
    private async IAsyncEnumerable<ImageEditEvent> ReadAsync([EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            await Task.CompletedTask; if (Failure is not null) throw Failure;
            foreach (var item in Events) { token.ThrowIfCancellationRequested(); yield return item; }
            OnTail?.Invoke(); if (TailFailure is not null) throw TailFailure;
        }
        finally { IteratorDisposed = true; }
    }
    public void Dispose() => Disposed = true;
}
