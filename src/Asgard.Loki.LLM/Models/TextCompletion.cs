namespace Asgard.Loki.LLM;

/// <summary>完整原生文本补全结果，保留 choices/text/logprobs/过滤/用量及扩展数据，不重排原生索引。</summary>
public sealed class TextCompletion(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
