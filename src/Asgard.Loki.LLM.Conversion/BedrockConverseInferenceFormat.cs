namespace Asgard.Loki.LLM.Conversion;

/// <summary>明确配置 Nova 专有设置在 Converse additionalModelRequestFields 中的位置。</summary>
public enum BedrockConverseInferenceFormat
{
    /// <summary>出现 topK 或 reasoningConfig 时必须显式选择包装方式。</summary>
    Unspecified = 0,
    /// <summary>直接使用 additionalModelRequestFields.topK/reasoningConfig。</summary>
    Direct = 1,
    /// <summary>使用 additionalModelRequestFields.inferenceConfig.topK/reasoningConfig。</summary>
    NestedInferenceConfig = 2
}
