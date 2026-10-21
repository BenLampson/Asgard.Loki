namespace Asgard.Loki.Rerank;

/// <summary>独立配置重排路由；不读取其他能力的模型、凭据或状态。</summary>
public sealed record RerankOptions
{
    /// <summary>公共模型到固定原生适配目标的映射。</summary>
    public required IReadOnlyList<RerankRoute> Routes { get; init; }
}
