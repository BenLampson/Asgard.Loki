namespace Asgard.Loki.Audio;

/// <summary>将一个对外音频模型映射到已独立配置的供应商目标。</summary>
public sealed record SpeechRoute
{
    /// <summary>对外音频模型路由名。</summary>
    public required string Model { get; init; }
    /// <summary>各自包含认证、地址与实际资源配置的适配实例。</summary>
    public required IReadOnlyList<ISpeechAdapter> Targets { get; init; }
}
