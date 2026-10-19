namespace Asgard.Loki.Audio;

/// <summary>仅属于文件转录的显式完整目标路由，不复用语音合成或翻译选择状态。</summary>
public sealed record AudioTranscriptionRoute
{
    /// <summary>公开模型路由名。</summary>
    public required string Model { get; init; }
    /// <summary>调用者持有的完整供应商目标列表。</summary>
    public required IReadOnlyList<IAudioTranscriptionAdapter> Targets { get; init; }
}
