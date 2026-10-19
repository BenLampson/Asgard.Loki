using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>单条已转换的原生 Bedrock 逻辑事件，不包含伪造的二进制传输帧。</summary>
public sealed class BedrockStreamConversionResult
{
    /// <summary>独立拍摄源/目标 JSON 和诊断，调用者后续修改不能改变已交付事件。</summary>
    internal BedrockStreamConversionResult(JsonObject data, JsonObject original, string? eventType, string? sourceEventName,
        IEnumerable<LlmConversionDiagnostic> diagnostics, LlmConversionContext? context)
    {
        Data = JsonSerializer.SerializeToElement(data); OriginalData = JsonSerializer.SerializeToElement(original);
        EventType = eventType; SourceEventName = sourceEventName; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); Context = context;
    }
    /// <summary>目标逻辑事件类型；不是声称已经存在的原始 AWS 传输头。</summary>
    public string? EventType { get; }
    /// <summary>目标原生 JSON：Converse 为载荷，Nova 为完整内层信封。</summary>
    public JsonElement Data { get; }
    /// <summary>未经转换的源 JSON，包括源 Nova 完整内层信封。</summary>
    public JsonElement OriginalData { get; }
    /// <summary>调用者提供的原始源事件名，Converse 不从载荷中的 type 猜测该值。</summary>
    public string? SourceEventName { get; }
    /// <summary>该事件交付时的累计损耗诊断快照。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics { get; }
    /// <summary>可选的原请求不可变上下文，仅本次转换使用。</summary>
    public LlmConversionContext? Context { get; }
}
