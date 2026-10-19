using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Asgard.Loki.Audio.Tests;

/// <summary>完全由应用提供的原生文件适配器，证明核心无 HTTP/凭据/供应商字段依赖。</summary>
internal sealed class StubAudioFileAdapter(string id = "file-one") : IStreamingAudioTranscriptionAdapter, IAudioTranslationAdapter, IDisposable
{
    public string Id { get; init; } = id;
    internal int Transcribes;
    internal int Translates;
    internal int Streams;
    internal AudioFileRequest? LastRequest { get; private set; }
    internal Action<AudioFileRequest>? OnRequest { get; init; }
    internal Exception? Failure { get; init; }
    internal IReadOnlyList<AudioTranscriptionEvent> Events { get; init; } = [Event(false), Event(true)];
    internal Exception? TailFailure { get; init; }
    internal Action? OnTail { get; init; }
    internal bool IteratorDisposed { get; private set; }
    internal bool Disposed { get; private set; }
    public Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Transcribes); Capture(request);
        return Failure is null ? Task.FromResult(Result("transcription-core.json", "application/json; charset=utf-8", request.Model)) : Task.FromException<AudioFileResult>(Failure);
    }
    public Task<AudioFileResult> TranslateAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Translates); Capture(request);
        return Failure is null ? Task.FromResult(Result("translation-core.txt", "text/vtt; charset=utf-8", request.Model)) : Task.FromException<AudioFileResult>(Failure);
    }
    public IAsyncEnumerable<AudioTranscriptionEvent> StreamAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Streams); Capture(request); return ReadAsync(cancellationToken); }
    private void Capture(AudioFileRequest request) { LastRequest = request; OnRequest?.Invoke(request); }
    private async IAsyncEnumerable<AudioTranscriptionEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await Task.CompletedTask;
            if (Failure is not null) throw Failure;
            foreach (var item in Events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            OnTail?.Invoke(); if (TailFailure is not null) throw TailFailure;
        }
        finally { IteratorDisposed = true; }
    }
    internal AudioFileResult Result(string fixture, string media, string model = "file")
        => new(Fixture(fixture), media, new(model, "native-file-model", Id));
    internal static byte[] Fixture(string fixture)
    {
        using var stream = typeof(StubAudioFileAdapter).Assembly.GetManifestResourceStream($"Asgard.Loki.Audio.Tests.Fixtures.{fixture}")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    internal static AudioTranscriptionEvent Event(bool done) => new(JsonSerializer.SerializeToElement(new
    { type = done ? "fixture.completed" : "fixture.delta", segment_id = "segment_fixture", future = new object?[] { null, false, "你好😀" } }), done,
        new("file", "native-file-model", "file-one"), "fixture.event", "event_fixture", 42);
    public void Dispose() => Disposed = true;
}
