namespace Asgard.Loki.Image.Ali;

/// <summary>阿里原生任务和完整目标的绑定，不包含原始凭据；不能作为授权凭证。</summary>
public sealed record AliImageTaskReference(string PublicModel, string TargetId, string TargetFingerprint, string TaskId);
