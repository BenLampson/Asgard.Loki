namespace Asgard.Loki.Video;

/// <summary>可由应用持久化的无凭据任务引用；绑定用于保持目标一致性，不替代上游访问控制。</summary>
public sealed record VideoTaskReference(string Model, string TargetId, string Fingerprint, string TaskId);
