namespace Asgard.Loki.Audio;

/// <summary>对外统一提供文本转语音能力。</summary>
public interface ISpeechClient
{
    /// <summary>聚合一次已经明确完成的合成结果。</summary>
    Task<SpeechResult> GenerateAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    /// <summary>读取合成过程的音频和元数据块。</summary>
    IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    /// <summary>聚合明确原生 JSON 调用，不猜测输出格式；旧的自定义客户端默认不支持。</summary>
    Task<NativeSpeechResult> GenerateNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<NativeSpeechResult>(new NotSupportedException("该语音客户端尚未实现原生 JSON 调用。"));
    /// <summary>显式使用原生 JSON 参数，路由必须全部支持可选契约，不自动选择其他目标。</summary>
    IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("该语音客户端尚未实现原生 JSON 调用。");
}
