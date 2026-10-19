namespace Asgard.Loki.Audio;

/// <summary>完整的供应商文件转录目标；目标固定绑定地址、实际模型、认证身份与实现。</summary>
public interface IAudioTranscriptionAdapter
{
    /// <summary>稳定的完整适配实例身份。</summary>
    string Id { get; }
    /// <summary>单次执行原生转录并保留完整结果；流为借用资源，不自动重试。</summary>
    Task<AudioFileResult> TranscribeAsync(AudioFileRequest request, CancellationToken cancellationToken = default);
}
