namespace Asgard.Loki.Audio;

/// <summary>供应商显式声明原生转录事件流能力，不从普通文件结果推导或模拟。</summary>
public interface IStreamingAudioTranscriptionAdapter : IAudioTranscriptionAdapter
{
    /// <summary>依原生顺序返回完整事件，只有协议确认的完成事件能带完成标志。</summary>
    IAsyncEnumerable<AudioTranscriptionEvent> StreamAsync(AudioFileRequest request, CancellationToken cancellationToken = default);
}
