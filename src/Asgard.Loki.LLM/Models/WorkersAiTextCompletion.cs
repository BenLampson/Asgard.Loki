namespace Asgard.Loki.LLM;

/// <summary>完整原生文本 result/success/errors/messages 封装，不合并工具或投影成 Chat。</summary>
public sealed class WorkersAiTextCompletion(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
