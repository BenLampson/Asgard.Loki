namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>独立模拟任意二进制分片、EOF 和流释放边界，不含供应商协议。</summary>
internal sealed class BodyProbeStream(byte[] bytes) : MemoryStream(bytes)
{
    /// <summary>EOF 时执行的边界动作。</summary>
    internal Action? OnEof { get; init; }
    /// <summary>释放时执行的边界动作。</summary>
    internal Action? OnDispose { get; init; }
    /// <summary>验证调用者在得到正文前已观察到流释放。</summary>
    internal bool Disposed { get; private set; }
    /// <summary>非可寻址网络流，不让 StreamContent 从 Length 推断正文长度。</summary>
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var count = base.Read(buffer.Span[..Math.Min(buffer.Length, 3)]);
        if (count == 0) OnEof?.Invoke(); return ValueTask.FromResult(count);
    }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); if (disposing && !Disposed) { Disposed = true; OnDispose?.Invoke(); }
    }
}
