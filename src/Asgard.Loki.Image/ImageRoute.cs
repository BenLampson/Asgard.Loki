namespace Asgard.Loki.Image;

/// <summary>将公共图像模型映射到独立配置的供应商目标。</summary>
public sealed record ImageRoute
{
    /// <summary>公共模型名称。</summary>
    public required string Model { get; init; }
    /// <summary>各自包含实际模型、地址和凭据的供应商目标。</summary>
    public required IReadOnlyList<IImageGenerationAdapter> Targets { get; init; }
}
