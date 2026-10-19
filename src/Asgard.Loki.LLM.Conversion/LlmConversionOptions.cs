namespace Asgard.Loki.LLM.Conversion;

/// <summary>由调用者明确选择的转换损耗策略与必需默认值。</summary>
public sealed record LlmConversionOptions
{
    /// <summary>默认拒绝任何损耗；仅显式开启后允许省略并返回诊断。</summary>
    public bool AllowLoss { get; init; }
    /// <summary>目标为 Claude 而来源没有输出上限时使用；不隐式猜测上限。</summary>
    public int? DefaultMaxTokens { get; init; }
    /// <summary>源 Schema 没有名称而目标 OpenAI 要求名称时使用；不猜测名称。</summary>
    public string? DefaultSchemaName { get; init; }
    /// <summary>数值推理预算无法直接表达为目标 effort 时的显式替代；替代仍产生损耗诊断。</summary>
    public string? TargetReasoningEffort { get; init; }
    /// <summary>目标 Claude 手动 thinking 模式使用的显式预算；不按 effort 比例推算。</summary>
    public int? TargetThinkingBudgetTokens { get; init; }
    /// <summary>Gemini 目标的文本输出外壳；默认保持原有报文，模型支持由调用者确认。</summary>
    public GeminiOutputFormat GeminiOutputFormat { get; init; }
    /// <summary>Gemini 目标函数参数方言；默认保留 JSON Schema，与文本输出格式独立。</summary>
    public GeminiFunctionSchemaFormat GeminiFunctionSchemaFormat { get; init; }
    /// <summary>可选的固定目标设置支持声明；仅校验跨协议最终请求，不补参数或查询模型。</summary>
    public LlmConversionTargetSettings? TargetSettings { get; init; }
    /// <summary>显式启用 Responses 自由文本 custom 到普通函数的可逆完整/流式调用转换。</summary>
    public bool EnableCustomToolConversion { get; init; }
    /// <summary>显式展开 Responses namespace 工具并使用请求上下文还原其原始身份。</summary>
    public bool EnableNamespacedToolConversion { get; init; }
    /// <summary>明确声明固定转换目标的原生搜索类型；默认禁用，不执行 SDK 侧搜索。</summary>
    public LlmHostedSearchTarget HostedSearchTarget { get; init; }
    /// <summary>调用者声明该目标支持原生搜索与函数工具同时使用；不自动猜测模型能力。</summary>
    public bool AllowHostedSearchWithFunctionTools { get; init; }
    /// <summary>固定 OpenAI 端点的引用索引单位，用于双向完整 Web grounding 映射和原生引用流范围校验。</summary>
    public LlmCitationIndexUnit OpenAiCitationIndexUnit { get; init; }
    /// <summary>启用可逆工具编码时，完整工具身份表及独立 namespace 分组表的容量上限。</summary>
    public int MaxToolConversionMappings { get; init; } = 128;
    /// <summary>启用可逆工具编码时，单次请求保留的历史调用 ID 总上限。</summary>
    public int MaxToolConversionHistoryCalls { get; init; } = 4096;
    /// <summary>启用可逆工具编码时，一次完整响应或流允许的 custom/普通调用总数。</summary>
    public int MaxToolConversionOutputCalls { get; init; } = 128;
    /// <summary>custom 输入、字符串结果及富结果数组 JSON 的最大 UTF-8 字节数；不截断内容。</summary>
    public int MaxCustomToolInputBytes { get; init; } = 1024 * 1024;
    /// <summary>单次跨协议流保留的原生引用条目上限；Responses 完成快照不重复计数，Gemini 新来源/支持共用额度。</summary>
    public int MaxStreamAnnotations { get; init; } = 1024;
    /// <summary>单次跨协议流保留引用/grounding 条目 JSON 的累计 UTF-8 字节上限，不截断或丢弃超限内容。</summary>
    public int MaxStreamAnnotationBytes { get; init; } = 1024 * 1024;
}
