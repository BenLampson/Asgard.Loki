namespace Asgard.Loki.LLM;

/// <summary>统一的 Responses 结果，保留全部输出项及状态。</summary>
public sealed class Response(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
