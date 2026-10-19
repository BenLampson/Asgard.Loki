namespace Asgard.Loki.LLM;

/// <summary>ConverseStream 原生事件载荷与独立传输事件名，不构造通用消息模型。</summary>
public sealed class BedrockConverseEvent(JsonElement data, LlmResultMetadata metadata, string? eventName = null)
    : LlmJsonEvent(data, metadata, eventName, null, null);
