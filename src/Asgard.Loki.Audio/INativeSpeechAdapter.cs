namespace Asgard.Loki.Audio;

/// <summary>可选的原生 JSON 语音调用契约，具体字段、格式和终态由音频供应商实现。</summary>
public interface INativeSpeechAdapter : ISpeechAdapter
{
    /// <summary>执行一次完整固定目标调用，保留原生参数，不回退为公共字符串语音请求。</summary>
    IAsyncEnumerable<SpeechChunk> StreamNativeAsync(NativeSpeechRequest request, CancellationToken cancellationToken = default);
}
