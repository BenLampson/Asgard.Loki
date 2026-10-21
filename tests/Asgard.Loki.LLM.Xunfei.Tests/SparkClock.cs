namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>固定 RFC1123 日期并手动触发计时器，不等待真实网络超时。</summary>
internal sealed class SparkClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1792454400);
    internal List<SparkTestTimer> Timers { get; } = [];
    public override DateTimeOffset GetUtcNow() => Now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    { var timer = new SparkTestTimer(callback, state); Timers.Add(timer); return timer; }
    internal void FireActiveTimers() { foreach (var timer in Timers.ToArray()) timer.Fire(); }
}
