namespace Asgard.Loki.Audio;

/// <summary>应用可持久化的无凭据音频任务引用，绑定完整目标和原生操作但不代替上游访问控制。</summary>
public sealed record AudioTaskReference(string Model, string TargetId, string Fingerprint, string TaskId, string Operation);
