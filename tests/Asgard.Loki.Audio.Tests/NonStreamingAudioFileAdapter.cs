namespace Asgard.Loki.Audio.Tests;

/// <summary>普通转录目标没有隐式流式能力，不能为了可用性被从路由中筛除。</summary>
internal sealed class NonStreamingAudioFileAdapter : IAudioTranscriptionAdapter
{
    public string Id => "non-streaming";
    internal int Calls { get; private set; }
    public Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default)
    { Calls++; throw new InvalidOperationException("Must not be dispatched by stream gate"); }
}
