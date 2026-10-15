namespace Asgard.Loki.Audio;

/// <summary>音频供应商独立的语音合成适配契约。</summary>
public interface ISpeechAdapter
{
    /// <summary>供应商实例标识。</summary>
    string Id { get; }
    /// <summary>返回有序音频块和供应商完成标志，不执行隐式重试。</summary>
    IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}
