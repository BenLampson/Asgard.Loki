namespace Asgard.Loki.Audio.Internal;

/// <summary>首次提交选择与既有任务恢复各自遵守完整目标绑定。</summary>
internal sealed class AudioTaskRouteState(AudioTaskTarget[] targets)
{
    private int _sequence = -1;
    /// <summary>只选择一次；不按操作筛选、重试或切换。</summary>
    internal AudioTaskTarget Select() => targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
    /// <summary>恢复引用必须仍有原目标和原配置，不触发新调度。</summary>
    internal AudioTaskTarget Resolve(AudioTaskReference reference)
    {
        var target = targets.SingleOrDefault(value => value.Id == reference.TargetId);
        if (target is null || target.Fingerprint(reference.Operation) != reference.Fingerprint)
            throw new InvalidOperationException("音频原目标已移除或配置改变，不允许重新调度。");
        target.Validate(); return target;
    }
}
