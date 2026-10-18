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
    /// <summary>有序原始语义消息。</summary>
    internal List<ConversionMessage> Messages { get; } = [];
    /// <summary>可直接映射的生成设置。</summary>
    internal JsonObject Settings { get; } = [];
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
