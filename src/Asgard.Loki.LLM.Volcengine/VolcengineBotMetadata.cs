namespace Asgard.Loki.LLM.Volcengine;

/// <summary>明确标注原生应用身份，不冒充远端实际模型或响应链归属。</summary>
/// <param name="TargetId">固定逻辑服务目标。</param>
/// <param name="BaseAddress">完整原生 API 根。</param>
/// <param name="BotId">已发布应用标识。</param>
/// <param name="CredentialId">凭据身份，不含 Key。</param>
/// <param name="Version">调用者配置版本。</param>
public sealed record VolcengineBotMetadata(string TargetId, Uri BaseAddress, string BotId, string CredentialId, string Version);
