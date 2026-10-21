namespace Asgard.Loki.LLM.OpenAI;

/// <summary>辅助搜索固定原生服务元数据，不冒充模型生成、Responses 归属或回答质量。</summary>
/// <param name="TargetId">应用定义的服务目标。</param>
/// <param name="BaseAddress">固定完整后端根。</param>
/// <param name="AccountId">明确选择的原生账户。</param>
/// <param name="CredentialId">借用外部凭据的稳定身份。</param>
/// <param name="Version">应用配置版本。</param>
public sealed record CodexSearchMetadata(string TargetId, Uri BaseAddress, string AccountId, string CredentialId, string Version);
