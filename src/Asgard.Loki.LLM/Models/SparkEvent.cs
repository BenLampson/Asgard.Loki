namespace Asgard.Loki.LLM;

/// <summary>一条完整星火原生 WebSocket JSON 消息，不投影为文本或 SSE；sid 保留在 Data.header.sid。</summary>
public sealed class SparkEvent(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
