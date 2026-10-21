namespace Asgard.Loki.LLM;

/// <summary>完整原生 Realtime JSON 事件快照；event_id、用量和状态保留于 Data。</summary>
public sealed class RealtimeEvent(JsonElement data, LlmResultMetadata metadata) : LlmJsonEvent(data, metadata, null, null, null);
