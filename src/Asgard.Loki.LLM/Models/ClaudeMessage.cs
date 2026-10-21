namespace Asgard.Loki.LLM;

/// <summary>保留 ClaudeMessages 完整原生 JSON 结果。</summary>
public sealed class ClaudeMessage(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
