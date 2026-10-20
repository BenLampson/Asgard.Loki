namespace Asgard.Loki.LLM.Dify;

/// <summary>固定原生应用服务元数据，不虚构底层实际模型或模型 Session。</summary>
/// <param name="TargetId">调用者配置的目标身份。</param>
/// <param name="BaseAddress">固定完整 API 根。</param>
/// <param name="Profile">明确的原生应用类别。</param>
/// <param name="CredentialId">凭据身份，不包含真实 Key。</param>
/// <param name="Version">应用配置版本。</param>
public sealed record DifyChatMetadata(string TargetId, Uri BaseAddress, DifyChatProfile Profile, string CredentialId, string Version);
