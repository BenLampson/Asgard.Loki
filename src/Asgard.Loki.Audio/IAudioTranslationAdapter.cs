namespace Asgard.Loki.Audio;

/// <summary>完整的供应商文件翻译目标；翻译语言、格式及参数保留供应商原生语义。</summary>
public interface IAudioTranslationAdapter
{
    /// <summary>稳定的完整适配实例身份。</summary>
    string Id { get; }
    /// <summary>单次执行原生翻译，不引入语言转换器、自动重试或 TTS/LLM 工作流。</summary>
    Task<AudioFileResult> TranslateAsync(AudioFileRequest request, CancellationToken cancellationToken = default);
}
