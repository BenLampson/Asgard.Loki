namespace Asgard.Loki.Video;

/// <summary>将公共视频路由映射到独立完整的供应商目标。</summary>
public sealed record VideoRoute
{
    /// <summary>公共模型或操作别名。</summary>
    public required string Model { get; init; }
    /// <summary>每个目标固定其服务器、实际模型、凭据与原生操作。</summary>
    public required IReadOnlyList<IVideoTaskAdapter> Targets { get; init; }
}
