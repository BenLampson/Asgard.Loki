namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>由测试明确推进的单次计时器，记录所有权，不使用 Thread.Sleep。</summary>
internal sealed class SparkTestTimer(TimerCallback callback, object? state) : ITimer
{
    private bool _fired;
    internal bool Disposed { get; private set; }
    public bool Change(TimeSpan dueTime, TimeSpan period) { _fired = false; return !Disposed; }
    internal void Fire() { if (!Disposed && !_fired) { _fired = true; callback(state); } }
    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
