namespace Asgard.Loki.Video;

/// <summary>视频任务的已确认生命周期；原生未知状态不能被推断成成功。</summary>
public enum VideoTaskState
{
    /// <summary>供应商返回未知状态，原文本仍保留。</summary>
    Unknown,
    /// <summary>提交已被接受，但尚未证实执行状态。</summary>
    Submitted,
    /// <summary>原生任务排队中。</summary>
    Queued,
    /// <summary>原生任务执行中。</summary>
    Running,
    /// <summary>供应商明确返回成功及原生结果。</summary>
    Succeeded,
    /// <summary>供应商明确返回执行失败。</summary>
    Failed,
    /// <summary>供应商明确返回任务取消。</summary>
    Cancelled,
    /// <summary>供应商明确返回任务过期。</summary>
    Expired
}
