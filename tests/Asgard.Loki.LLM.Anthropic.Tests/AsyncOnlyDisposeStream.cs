namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>异步释放故障不能被成功的同步 Dispose 掩盖。</summary>
internal sealed class AsyncOnlyDisposeStream(byte[] bytes, bool cancel, Action onDispose) : MemoryStream(bytes)
{
    internal bool AsyncDisposed { get; private set; }
    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        AsyncDisposed = true; base.Dispose(); onDispose();
        return cancel ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("async dispose"));
    }
}
