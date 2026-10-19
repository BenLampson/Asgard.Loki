namespace Asgard.Loki.LLM;

/// <summary>完整 Converse 原生 JSON，包括 output、stopReason、usage、metrics 和未知字段。</summary>
public sealed class BedrockConverseResponse(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
