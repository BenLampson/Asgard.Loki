namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>严格非寻址的借用上传/响应碎片流，观察读取与最终释放。</summary>
internal sealed class AzureImageReadStream(byte[] bytes) : Stream
{
    private int _offset;
    internal bool Disposed { get; private set; }
    internal bool Eof { get; private set; }
    internal int Consumed => _offset;
    internal int? FailAfter { get; init; }
    internal bool FailDispose { get; init; }
    internal Action? OnEof { get; init; }
    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new InvalidOperationException("length inspection forbidden");
    public override long Position { get => throw new InvalidOperationException("position inspection forbidden"); set => throw new InvalidOperationException("rewind forbidden"); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailAfter is { } at && _offset >= at) throw new IOException("fixture stream read failure");
        if (_offset == bytes.Length) { Eof = true; OnEof?.Invoke(); return ValueTask.FromResult(0); }
        var count = Math.Min(Math.Min(buffer.Length, 3), bytes.Length - _offset); bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
        return ValueTask.FromResult(count);
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override ValueTask DisposeAsync() { Disposed = true; return FailDispose ? ValueTask.FromException(new IOException("fixture stream disposal failure")) : base.DisposeAsync(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
