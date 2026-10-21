namespace Asgard.Loki.LLM.Perplexity;

/// <summary>显式选择原生 Sonar 的地址契约，不根据主机、模型或请求内容迁移。</summary>
public enum PerplexitySonarEndpoint
{
    /// <summary>在完整根下追加 chat/completions。</summary>
    ChatCompletions,
    /// <summary>在完整根下追加 v1/sonar。</summary>
    SonarV1
}
