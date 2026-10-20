namespace Asgard.Loki.Infrastructure.Tests;

/// <summary>不可寻址的逐字节传输替身，观察 EOF、取消和借用所有权。</summary>
internal sealed class SseProbeStream(byte[] bytes) : Stream
{
    private int _position;
    internal int Reads { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnEof { get; init; }
    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("No Length probe");
    public override long Position { get => throw new NotSupportedException("No Position probe"); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        Reads++; if (_position == bytes.Length) { OnEof?.Invoke(); return 0; }
        if (buffer.IsEmpty) return 0; buffer[0] = bytes[_position++]; return 1;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
