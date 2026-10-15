namespace Asgard.Loki.Audio;

/// <summary>独立配置音频能力，不借用 LLM 的状态或路由。</summary>
public sealed record AudioOptions
{
    /// <summary>已配置的语音合成路由。</summary>
    public required IReadOnlyList<SpeechRoute> SpeechRoutes { get; init; }
}
