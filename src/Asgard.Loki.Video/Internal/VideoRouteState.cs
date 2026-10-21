namespace Asgard.Loki.Video.Internal;

/// <summary>每个客户端分别持有首次提交选择状态；既有任务查询完全按绑定目标执行。</summary>
internal sealed class VideoRouteState(VideoTarget[] targets)
{
    private int _sequence = -1;
    /// <summary>首次提交选择一次，不自动重试或切换备用目标。</summary>
    internal VideoTarget Select() => targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
    /// <summary>恢复既有任务时必须仍存在同一完整目标及配置摘要。</summary>
    internal VideoTarget Resolve(VideoTaskReference reference)
    {
        var target = targets.SingleOrDefault(t => t.Id == reference.TargetId);
        if (target is null || target.Fingerprint != reference.Fingerprint) throw new InvalidOperationException("视频原目标已移除或配置改变，不允许重新调度。");
        target.Validate(); return target;
    }
}
