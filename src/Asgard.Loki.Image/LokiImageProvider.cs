namespace Asgard.Loki.Image;

/// <summary>图片生成模型注册表；Factory 创建时冻结集合，不按品牌猜测协议。</summary>
public sealed record LokiImageProvider
{
    /// <summary>公开模型注册。</summary>
    public required IReadOnlyList<LokiImageModel> Models { get; init; }
    /// <summary>可按名称复用的协议处理器，生命周期由应用管理。</summary>
    public IReadOnlyDictionary<string, ILokiImageGenerationHandler> Handlers { get; init; }
        = new Dictionary<string, ILokiImageGenerationHandler>();
}
