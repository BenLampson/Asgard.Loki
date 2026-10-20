namespace Asgard.Loki.LLM.Internal;

/// <summary>每次显式收发验证原绑定，不能在原会话消失时重新选择目标。</summary>
internal sealed class BoundRealtimeConnection(LlmInvocation invocation, LlmSessionBinding binding,
    IRealtimeConnection connection, LlmResultMetadata metadata) : IRealtimeConnection
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private int _reader;
    private int _closed;
    private int _disposed;
    /// <inheritdoc />
    public LlmResultMetadata Metadata { get; } = metadata;
    /// <inheritdoc />
    public ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message); ThrowIfClosed(); cancellationToken.ThrowIfCancellationRequested();
        LlmJsonInput.Node(message);
        return SendSnapshotAsync(message.DeepClone().AsObject(), cancellationToken);
    }
    /// <summary>先保存输入快照，再串行校验及写入；读写可并行。</summary>
    private async ValueTask SendSnapshotAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await invocation.ValidateBoundAsync(binding, null, cancellationToken).ConfigureAwait(false);
            ThrowIfClosed(); cancellationToken.ThrowIfCancellationRequested();
            await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("同一连接只允许一个并发读取者。");
        try
        {
            await invocation.ValidateBoundAsync(binding, null, cancellationToken).ConfigureAwait(false);
            ThrowIfClosed();
            await foreach (var item in connection.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                await invocation.ValidateBoundAsync(binding, null, cancellationToken).ConfigureAwait(false);
                ThrowIfClosed(); cancellationToken.ThrowIfCancellationRequested();
                yield return new RealtimeEvent(item.Data, Metadata);
            }
        }
        finally { Volatile.Write(ref _reader, 0); }
    }
    /// <inheritdoc />
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) await connection.CloseAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await connection.DisposeAsync().ConfigureAwait(false);
    }
    /// <summary>关闭或释放后不得继续收发。</summary>
    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("Realtime 连接已经关闭。");
    }
}
