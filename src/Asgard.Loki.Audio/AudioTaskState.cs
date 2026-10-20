namespace Asgard.Loki.Audio;

/// <summary>仅由音频供应商明确识别的任务生命周期，未知值不会推断为完成。</summary>
public enum AudioTaskState
{
    /// <summary>原生状态未知。</summary>
    Unknown,
    /// <summary>提交已接受，尚未确认执行或结果。</summary>
    Submitted,
    /// <summary>原生任务排队中。</summary>
    Queued,
    /// <summary>原生任务执行中。</summary>
    Running,
    /// <summary>供应商已确认成功及所需原生结果。</summary>
    Succeeded,
    /// <summary>供应商明确返回失败。</summary>
    Failed,
    /// <summary>供应商明确返回取消。</summary>
    Cancelled,
    /// <summary>供应商明确返回过期。</summary>
    Expired
}
