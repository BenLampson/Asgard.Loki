namespace Asgard.Loki.LLM;

/// <summary>声明 LLM 模块的完整上游组合与共享会话依赖。</summary>
public sealed record LlmOptions
{
    /// <summary>共享 Session 存储，必须显式提供。</summary>
    public required ILlmSessionStore SessionStore { get; init; }
    /// <summary>按需注册的供应商实例。</summary>
    public required IReadOnlyList<ILlmProvider> Providers { get; init; }
    /// <summary>对外模型及其完整目标列表。</summary>
    public required IReadOnlyList<LlmRoute> Routes { get; init; }
    /// <summary>由目标引用的凭据列表。</summary>
    public required IReadOnlyList<LlmCredential> Credentials { get; init; }
    /// <summary>仅对新会话生效的选择策略。</summary>
    public ILlmTargetSelector TargetSelector { get; init; } = new RoundRobinLlmTargetSelector();
}
