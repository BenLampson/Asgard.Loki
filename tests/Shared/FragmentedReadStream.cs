using System.Text;

namespace Asgard.Loki.Testing;

/// <summary>人为拆分字节读取，验证网络分块与 UTF-8 边界处理。</summary>
internal sealed class FragmentedReadStream(string content) : Stream
{
    private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(content));
    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _inner.Length;
    /// <inheritdoc />
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 3));
    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 3)]);
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
