namespace Asgard.Loki.Audio;

/// <summary>仅属于文件翻译的显式完整目标路由，不复用转录或 LLM 状态。</summary>
public sealed record AudioTranslationRoute
{
    /// <summary>公开模型路由名。</summary>
    public required string Model { get; init; }
    /// <summary>调用者持有的完整供应商目标列表。</summary>
    public required IReadOnlyList<IAudioTranslationAdapter> Targets { get; init; }
}
