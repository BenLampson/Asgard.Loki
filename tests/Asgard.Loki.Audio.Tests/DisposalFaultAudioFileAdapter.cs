namespace Asgard.Loki.Audio.Tests;

/// <summary>MoveNext 正常结束后，真正从 DisposeAsync 抛错，不能用迭代体尾部异常冒充释放失败。</summary>
internal sealed class DisposalFaultAudioFileAdapter : IStreamingAudioTranscriptionAdapter, IAsyncEnumerable<AudioTranscriptionEvent>, IAsyncEnumerator<AudioTranscriptionEvent>
{
    private int _index = -1;
    public string Id => "file-disposal-fault";
    public AudioTranscriptionEvent Current => StubAudioFileAdapter.Event(_index == 1);
    public Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<AudioTranscriptionEvent> StreamAsync(AudioFileRequest request, CancellationToken cancellationToken = default) => this;
    public IAsyncEnumerator<AudioTranscriptionEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_index < 2);
    public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("fixture disposal failed"));
}
