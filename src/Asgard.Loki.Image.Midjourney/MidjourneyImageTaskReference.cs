namespace Asgard.Loki.Image.Midjourney;

/// <summary>原生图片任务与完整代理目标的持久化关联，不包含原始凭据，也不是授权凭证。</summary>
public sealed record MidjourneyImageTaskReference(string TargetId, string TargetFingerprint, string TaskId);
