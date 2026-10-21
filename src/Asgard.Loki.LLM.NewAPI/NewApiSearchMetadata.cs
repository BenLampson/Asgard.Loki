namespace Asgard.Loki.LLM.NewAPI;

/// <summary>固定原生搜索目标身份，不包含密钥，不虚构生成 Session、响应链或资料质量。</summary>
/// <param name="TargetId">调用者定义的目标标识。</param>
/// <param name="BaseAddress">完整 API 根。</param>
/// <param name="UpstreamModel">显式选中的实际模型。</param>
/// <param name="CredentialId">调用者定义的稳定凭据引用。</param>
/// <param name="Version">调用者定义的目标配置版本。</param>
public sealed record NewApiSearchMetadata(string TargetId, Uri BaseAddress, string UpstreamModel, string CredentialId, string Version);
