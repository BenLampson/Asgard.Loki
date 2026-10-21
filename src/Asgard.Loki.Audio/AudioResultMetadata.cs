namespace Asgard.Loki.Audio;

/// <summary>区分公共路由、固定目标的实际模型与供应商实例；不包含认证信息。</summary>
public sealed record AudioResultMetadata(string PublicModel, string UpstreamModel, string ProviderId);
