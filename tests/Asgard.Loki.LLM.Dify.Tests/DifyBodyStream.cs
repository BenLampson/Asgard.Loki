namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>逐字节模拟真实网络碎片，并暴露 EOF、取消和释放故障。</summary>
internal sealed class DifyBodyStream(byte[] bytes) : Stream
{
    private int _position;
    internal int ReadCalls { get; private set; }
    internal bool Eof { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnEof { get; init; }
    internal Action? OnDispose { get; init; }
    internal bool FailAtEof { get; init; }
    internal bool FailDispose { get; init; }
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
    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++; if (_position == bytes.Length) { Eof = true; OnEof?.Invoke(); if (FailAtEof) throw new IOException("late read"); return 0; }
        if (count == 0) return 0;
        buffer[offset] = bytes[_position++]; return 1;
    }
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var temporary = new byte[1]; var count = Read(temporary, 0, Math.Min(buffer.Length, 1));
        if (count != 0) buffer.Span[0] = temporary[0]; return ValueTask.FromResult(count);
    }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed) { Disposed = true; OnDispose?.Invoke(); if (FailDispose) throw new IOException("body dispose"); }
        base.Dispose(disposing);
    }
    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
