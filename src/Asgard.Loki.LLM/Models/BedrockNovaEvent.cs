namespace Asgard.Loki.LLM;

/// <summary>完整 Nova 原生流式 JSON 信封，原生类型与外层传输名分别保留。</summary>
public sealed class BedrockNovaEvent(JsonElement data, LlmResultMetadata metadata, string? eventName = null, string? nativeType = null)
    : LlmJsonEvent(data, metadata, eventName, null, null)
{
    /// <summary>已解码的原生信封类型；否则保留原始传输名，不解释未知 type 字段。</summary>
    public override string? Type => nativeType ?? EventName;
}
