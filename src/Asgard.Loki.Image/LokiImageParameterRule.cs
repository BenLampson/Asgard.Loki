using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>明确声明某实际目标支持的统一参数及值域，不从模型名称推断。</summary>
public sealed record LokiImageParameterRule
{
    /// <summary>允许的原始统一值；空集合表示不限制枚举值。</summary>
    public IReadOnlyList<JsonElement> AllowedValues { get; init; } = [];
    /// <summary>数值下限，包含边界。</summary>
    public double? Minimum { get; init; }
    /// <summary>数值上限，包含边界。</summary>
    public double? Maximum { get; init; }
}
