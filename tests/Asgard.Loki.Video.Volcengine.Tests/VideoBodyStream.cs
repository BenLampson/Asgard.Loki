namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>模拟视频 JSON 字节分片、最终读取和释放故障，不连接网络。</summary>
internal sealed class VideoBodyStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);
    /// <summary>可控的最终读取故障。</summary>
    internal bool FailAtEof { get; init; }
    /// <summary>可控的释放故障。</summary>
    internal bool FailDispose { get; init; }
    /// <summary>最终读取的取消边界。</summary>
    internal Action? OnEof { get; init; }
    /// <summary>资源是否已释放。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override int Read(byte[] buffer,int offset,int count)
    {
        var result = _inner.Read(buffer,offset,Math.Min(count,3));
        if (result == 0) { OnEof?.Invoke(); if (FailAtEof) throw new IOException("late video read"); }
        return result;
    }
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var temporary = new byte[Math.Min(buffer.Length,3)]; var count = Read(temporary,0,temporary.Length);
        temporary.AsSpan(0,count).CopyTo(buffer.Span); return ValueTask.FromResult(count);
    }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed) { Disposed = true; _inner.Dispose(); if (FailDispose) throw new IOException("video body disposal"); }
        base.Dispose(disposing);
    }
    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
}
