namespace Asgard.Loki.Audio;

/// <summary>对外统一提供文本转语音能力。</summary>
public interface ISpeechClient
{
    /// <summary>聚合一次已经明确完成的合成结果。</summary>
    Task<SpeechResult> GenerateAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    /// <summary>读取合成过程的音频和元数据块。</summary>
    IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}
