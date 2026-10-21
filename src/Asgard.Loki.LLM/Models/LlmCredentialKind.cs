namespace Asgard.Loki.LLM;

/// <summary>明确声明凭据的认证方式，不根据地址或模型名称自动降级认证。</summary>
public enum LlmCredentialKind
{
    /// <summary>使用原生供应商的 API Key 认证；保持既有默认行为。</summary>
    ApiKey,
    /// <summary>由调用者明确配置的无认证身份，仍使用稳定 ID 参与完整目标绑定。</summary>
    Anonymous,
    /// <summary>由供应商借用外部认证源取得短期凭据，核心只绑定稳定主体身份。</summary>
    External
}
