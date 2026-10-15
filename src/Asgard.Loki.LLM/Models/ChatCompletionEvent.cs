namespace Asgard.Loki.LLM;

/// <summary>统一的 Chat Completions 流式分块。</summary>
public sealed class ChatCompletionEvent(JsonElement data, LlmResultMetadata metadata,
    string? eventName = null, string? eventId = null, long? retryMilliseconds = null) : LlmJsonEvent(data, metadata, eventName, eventId, retryMilliseconds);
