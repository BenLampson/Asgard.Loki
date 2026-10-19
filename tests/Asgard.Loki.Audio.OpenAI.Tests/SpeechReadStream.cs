namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>不解码的分片字节流，可在已交付部分音频后注入读取、释放或取消失败。</summary>
internal sealed class SpeechReadStream(byte[] bytes) : Stream
{
    private int _offset;
    internal int FragmentSize { get; init; } = 3;
    internal bool FailRead { get; init; }
    internal int? FailAfterBytes { get; init; }
    internal bool FailDispose { get; init; }
    internal Action? OnEof { get; init; }
    internal Func<CancellationToken, Task>? BeforeRead { get; init; }
    internal bool Disposed { get; private set; }
    internal int Reads { get; private set; }
    internal bool Eof { get; private set; }
    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reads++; cancellationToken.ThrowIfCancellationRequested();
        if (BeforeRead is not null) await BeforeRead(cancellationToken);
        if (FailRead && _offset > 0 || FailAfterBytes is { } threshold && _offset >= threshold) throw new IOException("fixture read failure");
        if (_offset == bytes.Length) { Eof = true; OnEof?.Invoke(); return 0; }
        var count = Math.Min(Math.Min(FragmentSize, buffer.Length), bytes.Length - _offset);
        bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return FailDispose ? ValueTask.FromException(new IOException("fixture disposal failure")) : base.DisposeAsync();
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
