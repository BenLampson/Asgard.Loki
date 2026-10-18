namespace Asgard.Loki.LLM;

/// <summary>保留 Gemini 原生流式事件与传输元数据。</summary>
public sealed class GeminiContentEvent(JsonElement data, LlmResultMetadata metadata,
    string? eventName = null, string? eventId = null, long? retryMilliseconds = null) : LlmJsonEvent(data, metadata, eventName, eventId, retryMilliseconds);
