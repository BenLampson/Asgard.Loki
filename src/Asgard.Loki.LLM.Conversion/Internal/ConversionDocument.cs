using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>在单次显式转换内携带可映射数据及诊断。</summary>
internal sealed class ConversionDocument
{
    /// <summary>实际模型名。</summary>
    internal string Model { get; set; } = "";
    /// <summary>原始响应 ID 或流内稳定生成的 ID。</summary>
    internal string Id { get; set; } = "";
    /// <summary>原始创建时间；源协议未提供时以零表示未知。</summary>
    internal long Created { get; set; }
    /// <summary>本次 OpenAI 引用范围使用的显式单位；未知时不能猜测 Unicode 偏移。</summary>
    internal LlmCitationIndexUnit CitationIndexUnit { get; set; }
    /// <summary>有序原始语义消息。</summary>
    internal List<ConversionMessage> Messages { get; } = [];
    /// <summary>可直接映射的生成设置。</summary>
    internal JsonObject Settings { get; } = [];
    /// <summary>推理模式、精确预算、定性强度和输出意图保持独立。</summary>
    internal JsonObject Reasoning { get; } = [];
    /// <summary>结构化输出规范，Schema 保留原始 JSON，不做降级简化。</summary>
    internal JsonObject? OutputFormat { get; set; }
    /// <summary>源文本结构化输出承诺按 properties 顺序生成；普通函数参数不继承此承诺。</summary>
    internal bool OutputSchemaOrdered { get; set; }
    /// <summary>规范化函数定义。</summary>
    internal JsonArray Tools { get; } = [];
    /// <summary>规范化工具选择。</summary>
    internal JsonNode? ToolChoice { get; set; }
    /// <summary>结束原因，保持已知语义。</summary>
    internal string Finish { get; set; } = "stop";
    /// <summary>输入、输出及缓存 token 计数。</summary>
    internal JsonObject Usage { get; } = [];
    /// <summary>显式损耗记录。</summary>
    internal List<LlmConversionDiagnostic> Diagnostics { get; } = [];
    /// <summary>Gemini 原生或生成的工具调用关联状态。</summary>
    internal ToolCallTracker GeminiCalls { get; } = new();
    /// <summary>提前保留输入中的显式 ID，防止生成标识碰撞。</summary>
    internal HashSet<string> ReservedCallIds { get; } = new(StringComparer.Ordinal);
}
