namespace Asgard.Loki.LLM;

/// <summary>混元原生平铺 SSE JSON 与传输元数据，不套用完整结果的 Response 信封。</summary>
public sealed class HunyuanEvent(JsonElement data, LlmResultMetadata metadata,
    string? eventName = null, string? eventId = null, long? retryMilliseconds = null) : LlmJsonEvent(data, metadata, eventName, eventId, retryMilliseconds);
