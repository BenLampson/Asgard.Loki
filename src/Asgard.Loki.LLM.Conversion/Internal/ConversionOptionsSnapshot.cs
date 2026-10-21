namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>复制调用者拥有的集合并规范集合顺序，保证配置与绑定身份不被外部修改。</summary>
internal static class ConversionOptionsSnapshot
{
    /// <summary>标量随 record 复制；嵌套声明和列表独立持有，不保留可变数组引用。</summary>
    internal static LlmConversionOptions Capture(LlmConversionOptions? options)
    {
        var snapshot = options is null ? new LlmConversionOptions() : options with { };
        if (snapshot.TargetSettings is not { } settings) return snapshot;
        return snapshot with
        {
            TargetSettings = settings with
            {
                AllowedReasoningEfforts = Copy(settings.AllowedReasoningEfforts),
                AllowedThinkingModes = Copy(settings.AllowedThinkingModes),
                AllowedOutputFormats = Copy(settings.AllowedOutputFormats)
            }
        };
    }

    /// <summary>列表按集合语义排序去重；省略与空集合仍保持不同，不推断额外允许值。</summary>
    private static IReadOnlyList<string>? Copy(IReadOnlyList<string>? source)
        => source is null ? null : Array.AsReadOnly(source.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
}
