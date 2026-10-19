namespace Asgard.Loki.LLM.Conversion;

/// <summary>由调用者明确选择的转换损耗策略与必需默认值。</summary>
public sealed record LlmConversionOptions
{
    /// <summary>Nova/Converse 跨协议请求的显式 Nova 外壳；缺省不影响已有四协议绑定字节。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public BedrockNovaRequestFormat BedrockNovaRequestFormat { get; init; }
    /// <summary>Nova 专有设置的明确 Converse 包装，不推断模型能力或静默迁移其他位置。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public BedrockConverseInferenceFormat BedrockConverseInferenceFormat { get; init; }
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
    /// <summary>显式 Claude/Gemini 工具错误格式；不为 Chat/Responses 发明错误标志，也不启用工具执行。</summary>
    public LlmToolErrorFormat ToolErrorFormat { get; init; }
    /// <summary>显式错误格式的原始文本或完整紧凑 JSON 的 UTF-8 字节上限；超限失败，不截断。</summary>
    public int MaxToolErrorBytes { get; init; } = 1024 * 1024;
    /// <summary>明确声明固定转换目标的原生搜索类型；默认禁用，不执行 SDK 侧搜索。</summary>
    public LlmHostedSearchTarget HostedSearchTarget { get; init; }
    /// <summary>调用者声明该目标支持原生搜索与函数工具同时使用；不自动猜测模型能力。</summary>
    public bool AllowHostedSearchWithFunctionTools { get; init; }
    /// <summary>固定 OpenAI 端点的引用索引单位，用于完整 Web grounding、显式 Gemini 引用流映射及源引用流范围校验。</summary>
    public LlmCitationIndexUnit OpenAiCitationIndexUnit { get; init; }
    /// <summary>显式累计引用流约定；可选 Responses 文本块顺序拼接，还需声明 OpenAI 端点索引单位。</summary>
    public GeminiGroundingStreamMode GeminiGroundingStreamMode { get; init; }
    /// <summary>显式声明 text/plain 文档按 UTF-8 转换；保留文件容器，Chat 没有对应文件输入。</summary>
    public LlmTextFileEncoding TextFileEncoding { get; init; }
    /// <summary>单个纯文本文件的解码 UTF-8 字节上限；分配解码缓冲之前验证，不影响原生调用或 PDF。</summary>
    public int MaxTextFileBytes { get; init; } = 1024 * 1024;
    /// <summary>启用可逆工具编码时，完整工具身份表及独立 namespace 分组表的容量上限。</summary>
    public int MaxToolConversionMappings { get; init; } = 128;
    /// <summary>启用可逆工具编码时，单次请求保留的历史调用 ID 总上限。</summary>
    public int MaxToolConversionHistoryCalls { get; init; } = 4096;
    /// <summary>启用可逆工具编码时，一次完整响应或流允许的 custom/普通调用总数。</summary>
    public int MaxToolConversionOutputCalls { get; init; } = 128;
    /// <summary>custom 输入、字符串结果及富结果数组 JSON 的最大 UTF-8 字节数；不截断内容。</summary>
    public int MaxCustomToolInputBytes { get; init; } = 1024 * 1024;
    /// <summary>单次流保留条目上限；Responses 不重复计快照，Gemini 来源/支持共用额度，映射输出引用另用同值独立限额。</summary>
    public int MaxStreamAnnotations { get; init; } = 1024;
    /// <summary>单次流保留引用/grounding JSON 的累计 UTF-8 字节上限；映射输出另用同值独立限额，不截断或丢弃。</summary>
    public int MaxStreamAnnotationBytes { get; init; } = 1024 * 1024;
}
