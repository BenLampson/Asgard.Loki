namespace Asgard.Loki.LLM;

/// <summary>完整 Nova 原生 JSON，保留 output、stopReason、用量、性能及未知数据。</summary>
public sealed class BedrockNovaResponse(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
