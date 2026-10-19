namespace Asgard.Loki.Audio;

/// <summary>独立配置音频能力，不借用 LLM 的状态或路由。</summary>
public sealed record AudioOptions
{
    /// <summary>已配置的语音合成路由。</summary>
    public IReadOnlyList<SpeechRoute> SpeechRoutes { get; init; } = [];
    /// <summary>独立文件转录路由，可在不配置合成时单独注册。</summary>
    public IReadOnlyList<AudioTranscriptionRoute> TranscriptionRoutes { get; init; } = [];
    /// <summary>独立文件翻译路由，不要求存在转录或合成路由。</summary>
    public IReadOnlyList<AudioTranslationRoute> TranslationRoutes { get; init; } = [];
}
