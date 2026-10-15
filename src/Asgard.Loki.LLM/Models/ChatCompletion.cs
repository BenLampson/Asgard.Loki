namespace Asgard.Loki.LLM;

/// <summary>统一的 Chat Completions 结果，保留完整 choices、usage 和扩展数据。</summary>
public sealed class ChatCompletion(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
