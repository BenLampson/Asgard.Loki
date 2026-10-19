namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>不可 seek 的原始分片流，独立观察上传/下载及读取/释放失败。</summary>
internal sealed class EditReadStream(byte[] bytes) : Stream
{
    private int _offset;
    internal bool FailRead { get; init; }
    internal int? FailAfterBytes { get; init; }
    internal int FragmentSize { get; init; } = 3;
    internal bool FailDispose { get; init; }
    internal Action? OnEof { get; init; }
    internal bool Disposed { get; private set; }
    internal int Reads { get; private set; }
    internal bool Eof { get; private set; }
    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("fixture forbids length");
    public override long Position { get => throw new NotSupportedException("fixture forbids position"); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reads++; cancellationToken.ThrowIfCancellationRequested();
        if (FailRead && _offset > 0 || FailAfterBytes is { } limit && _offset >= limit) return ValueTask.FromException<int>(new IOException("fixture read failure"));
        if (_offset == bytes.Length) { Eof = true; OnEof?.Invoke(); return ValueTask.FromResult(0); }
        var count = Math.Min(Math.Min(FragmentSize, buffer.Length), bytes.Length - _offset);
        bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return ValueTask.FromResult(count);
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override ValueTask DisposeAsync()
    { Disposed = true; return FailDispose ? ValueTask.FromException(new IOException("fixture disposal failure")) : base.DisposeAsync(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
