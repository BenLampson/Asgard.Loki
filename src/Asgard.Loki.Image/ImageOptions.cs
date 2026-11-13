namespace Asgard.Loki.Image;

/// <summary>独立配置图像能力。</summary>
public sealed record ImageOptions
{
    /// <summary>可选统一图片生成工厂，与既有生成和编辑路由独立注册。</summary>
    public LokiImageFactory? GenerationFactory { get; init; }
    /// <summary>公共模型到生成目标的映射。</summary>
    public IReadOnlyList<ImageRoute> Routes { get; init; } = [];
    /// <summary>独立的图像编辑路由，可不配置文本生成；目标不从 Routes 中筛选或借用。</summary>
    public IReadOnlyList<ImageEditRoute> EditRoutes { get; init; } = [];
}
