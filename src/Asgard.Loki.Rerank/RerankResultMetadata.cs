namespace Asgard.Loki.Rerank;

/// <summary>区分公共路由、固定目标的实际模型与供应商实例，不包含凭据。</summary>
public sealed record RerankResultMetadata(string PublicModel, string UpstreamModel, string ProviderId);
