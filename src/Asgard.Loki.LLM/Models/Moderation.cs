namespace Asgard.Loki.LLM;

/// <summary>保留完整原生分类结果、类别分数、输入类型和扩展字段，不阈值判断或补造缺失标签。</summary>
public sealed class Moderation(JsonElement data, LlmResultMetadata metadata) : LlmJsonResult(data, metadata);
