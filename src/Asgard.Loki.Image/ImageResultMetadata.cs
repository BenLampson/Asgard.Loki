namespace Asgard.Loki.Image;

/// <summary>区分公共图像模型与实际执行的供应商模型。</summary>
public sealed record ImageResultMetadata(string PublicModel, string UpstreamModel, string ProviderId);
