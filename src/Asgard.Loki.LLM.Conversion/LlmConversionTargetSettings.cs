namespace Asgard.Loki.LLM.Conversion;

/// <summary>调用者对固定转换目标的设置支持声明；不查询模型、不改变请求或代表账号权限。</summary>
public sealed record LlmConversionTargetSettings
{
    /// <summary>允许的目标原生强度字段小写值；null 不限制，空集合拒绝任何显式字段，包括 none。</summary>
    public IReadOnlyList<string>? AllowedReasoningEfforts { get; init; }
    /// <summary>Claude/Gemini 显式 thinking 控制的 manual、adaptive、disabled 模式；null 不限制。</summary>
    public IReadOnlyList<string>? AllowedThinkingModes { get; init; }
    /// <summary>允许显式设置的 text、json_object、json_schema 输出意图；null 不限制。</summary>
    public IReadOnlyList<string>? AllowedOutputFormats { get; init; }
    /// <summary>已提供的原生输出 token 上限不得超过此值；不补默认值，也不是计费配额。</summary>
    public int? MaximumOutputTokens { get; init; }
    /// <summary>显式正数手动推理预算的最小值；不约束关闭的零或动态的 -1。</summary>
    public int? MinimumThinkingBudgetTokens { get; init; }
    /// <summary>显式正数手动推理预算的最大值；不钳制预算或提升输出上限。</summary>
    public int? MaximumThinkingBudgetTokens { get; init; }
    /// <summary>false 拒绝任何显式 temperature/top-p/top-k 字段，包括零和 null；true 不创造字段。</summary>
    public bool? SupportsSampling { get; init; }
    /// <summary>false 拒绝显式并行工具控制字段，包括 false；不执行工具。</summary>
    public bool? SupportsParallelToolControl { get; init; }
    /// <summary>false 拒绝显式 summary/display/includeThoughts 控制字段，包括 null/false。</summary>
    public bool? SupportsReasoningOutputControl { get; init; }
}
