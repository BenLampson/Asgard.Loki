using System.Text;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>不可 seek 的分片 SSE 响应，支持原生终态之后的读取/释放失败和取消。</summary>
internal sealed class GroundingCitationReadStream(string source) : Stream
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(source);
    private int _offset;
    internal bool FailAtEof { get; init; }
    internal bool FailDispose { get; init; }
    internal Action? OnEof { get; init; }
    internal bool Eof { get; private set; }
    internal bool Disposed { get; private set; }
    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_offset == _bytes.Length)
        {
            if (FailAtEof) return ValueTask.FromException<int>(new IOException("fixture EOF read failure"));
            Eof = true; OnEof?.Invoke(); return ValueTask.FromResult(0);
        }
        var count = Math.Min(Math.Min(buffer.Length, 3), _bytes.Length - _offset);
        _bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return ValueTask.FromResult(count);
    }
    public override ValueTask DisposeAsync()
    { Disposed = true; return FailDispose ? ValueTask.FromException(new IOException("fixture dispose failure")) : base.DisposeAsync(); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
