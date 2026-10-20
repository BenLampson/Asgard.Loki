namespace Asgard.Loki.LLM;

/// <summary>原生文本补全 SSE 事件，原始 JSON 与 SSE 元数据保持独立。</summary>
public sealed class TextCompletionEvent(JsonElement data, LlmResultMetadata metadata,
    string? eventName = null, string? eventId = null, long? retryMilliseconds = null) : LlmJsonEvent(data, metadata, eventName, eventId, retryMilliseconds);
