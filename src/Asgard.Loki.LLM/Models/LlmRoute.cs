namespace Asgard.Loki.LLM;

/// <summary>将一个对外模型名映射到多个完整上游目标。</summary>
public sealed record LlmRoute
{
    /// <summary>调用者使用的逻辑模型名。</summary>
    public required string Model { get; init; }
    /// <summary>可包含不同服务器、模型、凭据和供应商的目标。</summary>
    public required IReadOnlyList<LlmTarget> Targets { get; init; }
}
