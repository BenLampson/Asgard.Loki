namespace Asgard.Loki.Infrastructure;

/// <summary>在解码分配之前验证原始字节预算及可选长度，始终借用输入流。</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _source;
    private readonly long _maximum;
    private readonly long? _expected;
    private long _received;
    private bool _disposed;
    /// <summary>验证显式预算，不读取源 Length/Position，也不获取源的释放所有权。</summary>
    internal BoundedReadStream(Stream source, long maximum, long? expected)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximum, 0);
        if (expected < 0) throw new ArgumentOutOfRangeException(nameof(expected));
        if (expected > maximum) throw new InvalidDataException("原始流声明长度超过字节预算。");
        _source = source; _maximum = maximum; _expected = expected;
    }
    /// <inheritdoc />
    public override bool CanRead => !_disposed && _source.CanRead;
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
    { ValidateBufferArguments(buffer, offset, count); return Read(buffer.AsSpan(offset, count)); }
    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); if (buffer.IsEmpty) return 0;
        return Check(_source.Read(buffer));
    }
    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested(); if (buffer.IsEmpty) return 0;
        var count = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return Check(count);
    }
    /// <summary>不溢出计数；EOF 也需实际长度相等，不能将预算耗尽当作连接结束。</summary>
    private int Check(int count)
    {
        if (count < 0 || count > _maximum - _received || _expected is { } size && count > size - _received)
            throw new InvalidDataException("原始流超过字节预算或声明长度。");
        _received += count;
        if (count == 0 && _expected is { } expected && _received != expected) throw new InvalidDataException("原始流未达到声明长度。");
        return count;
    }
    /// <summary>只结束包装器，不关闭或回卷借用的源。</summary>
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
