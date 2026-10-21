namespace Asgard.Loki.LLM.Conversion;

/// <summary>调用者明确选择的 Converse 模型参数方言，不探测或按模型名称推断支持能力。</summary>
public enum BedrockConverseModelDialect
{
    /// <summary>不启用模型专有设置转换。</summary>
    Unspecified = 0,
    /// <summary>Anthropic 请求设置，以及 Claude/Converse 的原生签名和加密块。</summary>
    Anthropic = 1,
    /// <summary>Amazon Nova2 reasoningConfig 与 topK；包装位置还需单独明确选择。</summary>
    AmazonNova2 = 2
}
