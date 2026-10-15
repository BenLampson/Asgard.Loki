namespace Asgard.Loki.Tests;

/// <summary>验证模块初始化及资源释放的测试对象。</summary>
internal sealed class TrackedModule : IAsyncDisposable
{
    /// <summary>已执行的释放次数。</summary>
    internal int Disposals { get; private set; }
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
