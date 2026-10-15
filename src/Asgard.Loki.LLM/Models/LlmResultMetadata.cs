namespace Asgard.Loki.LLM;

/// <summary>区分公共模型与真实上游身份，不包含密钥。</summary>
public sealed record LlmResultMetadata(string PublicModel, string UpstreamModel, string ProviderId, string TargetId);
