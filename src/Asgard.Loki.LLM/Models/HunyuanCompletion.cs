namespace Asgard.Loki.LLM;

/// <summary>完整混元原生 Response 信封及路由元数据；原生 Id 保留在 Data.Response.Id。</summary>
public sealed class HunyuanCompletion(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
