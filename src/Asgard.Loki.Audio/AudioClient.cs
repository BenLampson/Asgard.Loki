namespace Asgard.Loki.Audio;

/// <summary>组织独立的音频能力入口。</summary>
public sealed class AudioClient
{
    private readonly ISpeechClient? _speech;
    private readonly AudioTranscriptionClient? _transcriptions;
    private readonly AudioTranslationClient? _translations;
    /// <summary>接收已完成配置校验的语音客户端。</summary>
    internal AudioClient(ISpeechClient? speech, AudioTranscriptionClient? transcriptions, AudioTranslationClient? translations)
    { _speech = speech; _transcriptions = transcriptions; _translations = translations; }
    /// <summary>文本转语音能力。</summary>
    public ISpeechClient Speech => _speech ?? throw new InvalidOperationException("未配置语音合成路由。");
    /// <summary>独立文件转录操作；未配置时访问即失败。</summary>
    public AudioTranscriptionClient Transcriptions => _transcriptions ?? throw new InvalidOperationException("未配置文件转录路由。");
    /// <summary>独立文件翻译操作；未配置时访问即失败。</summary>
    public AudioTranslationClient Translations => _translations ?? throw new InvalidOperationException("未配置文件翻译路由。");
}
