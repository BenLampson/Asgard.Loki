namespace Asgard.Loki.Image;

/// <summary>独立配置图像能力。</summary>
public sealed record ImageOptions
{
    /// <summary>公共模型到生成目标的映射。</summary>
    public required IReadOnlyList<ImageRoute> Routes { get; init; }
}
