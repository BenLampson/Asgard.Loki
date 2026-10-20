namespace Asgard.Loki.Infrastructure;

/// <summary>在写入借用传输流之前限制累计字节，不关闭、回卷或清空底层流。</summary>
internal sealed class BoundedWriteStream(Stream destination, long maximumBytes) : Stream
{
    private long _written;
    /// <summary>已完成写入的字节数，供外层验证声明长度。</summary>
    internal long BytesWritten => _written;
    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => destination.CanWrite;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); destination.Write(buffer); _written += buffer.Length; }
    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Check(buffer.Length); await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); _written += buffer.Length; }
    /// <summary>先验证剩余预算，避免溢出或超限字节到达上游。</summary>
    private void Check(int count) { if (count > maximumBytes - _written) throw new InvalidDataException("HTTP 上传正文超过显式字节上限。"); }
    /// <inheritdoc />
    public override void Flush() => destination.Flush();
    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}
