namespace Asgard.Loki.Image;

/// <summary>一个公开图片模型及其可选择的完整上游集合。</summary>
public sealed record LokiImageModel
{
    /// <summary>业务使用的公开模型名。</summary>
    public required string Name { get; init; }
    /// <summary>可选完整上游，顺序由选择器解释。</summary>
    public required IReadOnlyList<LokiImageUpstreamProvider> Providers { get; init; }
    /// <summary>可选自定义选择器，由应用管理生命周期；默认每客户端独立轮询。</summary>
    public ILokiImageProviderSelector? Selector { get; init; }
}
