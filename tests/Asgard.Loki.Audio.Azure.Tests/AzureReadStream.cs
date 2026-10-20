namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>非寻址碎片流用于观察借用上传及完整响应最终边界。</summary>
internal sealed class AzureReadStream(byte[] bytes) : Stream
{
    private int _offset;
    internal bool Disposed { get; private set; }
    internal bool Eof { get; private set; }
    internal int Consumed => _offset;
    internal int FragmentSize { get; init; } = 3;
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
        if (FailAfter is { } threshold && _offset >= threshold) throw new IOException("read fault");
        if (_offset == bytes.Length) { Eof = true; OnEof?.Invoke(); return ValueTask.FromResult(0); }
        var count = Math.Min(Math.Min(buffer.Length, FragmentSize), bytes.Length - _offset); bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
        return ValueTask.FromResult(count);
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override ValueTask DisposeAsync() { Disposed = true; return FailDispose ? ValueTask.FromException(new IOException("dispose fault")) : base.DisposeAsync(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
