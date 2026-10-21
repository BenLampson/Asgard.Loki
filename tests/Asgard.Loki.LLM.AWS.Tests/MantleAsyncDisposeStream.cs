namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>异步释放独有的异常或取消不能被后续同步释放掩盖。</summary>
internal sealed class MantleAsyncDisposeStream(byte[] bytes, Action afterDispose, bool fail) : MemoryStream(bytes)
{
    internal bool AsyncDisposed { get; private set; }
    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        AsyncDisposed = true; base.Dispose(); afterDispose(); return fail ? ValueTask.FromException(new IOException("async dispose")) : ValueTask.CompletedTask;
    }
}
