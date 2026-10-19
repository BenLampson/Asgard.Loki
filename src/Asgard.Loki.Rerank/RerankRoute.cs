namespace Asgard.Loki.Rerank;

/// <summary>将公共重排模型映射到完整且独立配置的上游目标。</summary>
public sealed record RerankRoute
{
    /// <summary>公共路由名，采用区分大小写的精确匹配。</summary>
    public required string Model { get; init; }
    /// <summary>调用者借给模块使用的适配器，每个实例封装完整上游配置。</summary>
    public required IReadOnlyList<IRerankAdapter> Targets { get; init; }
}
