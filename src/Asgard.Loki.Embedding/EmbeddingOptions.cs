namespace Asgard.Loki.Embedding;

/// <summary>独立配置嵌入路由；不读取其他能力的模型、凭据或状态。</summary>
public sealed record EmbeddingOptions
{
    /// <summary>公共模型到固定原生适配目标的映射。</summary>
    public required IReadOnlyList<EmbeddingRoute> Routes { get; init; }
}
