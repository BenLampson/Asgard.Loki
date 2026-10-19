namespace Asgard.Loki.Image;

/// <summary>仅属于图像编辑的完整目标路由，不复用生成、LLM 或音频的目标/状态。</summary>
public sealed record ImageEditRoute
{
    /// <summary>公开编辑模型路由。</summary>
    public required string Model { get; init; }
    /// <summary>固定的完整编辑目标列表，顺序在注册时复制。</summary>
    public required IReadOnlyList<IImageEditAdapter> Targets { get; init; }
}
