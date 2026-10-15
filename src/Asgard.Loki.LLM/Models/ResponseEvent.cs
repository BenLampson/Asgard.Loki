namespace Asgard.Loki.LLM;

/// <summary>统一的 Responses 流式事件，包括未知扩展事件。</summary>
public sealed class ResponseEvent(JsonElement data, LlmResultMetadata metadata,
    string? eventName = null, string? eventId = null, long? retryMilliseconds = null) : LlmJsonEvent(data, metadata, eventName, eventId, retryMilliseconds);
