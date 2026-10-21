namespace Asgard.Loki.LLM.GoogleVertex.Internal;

/// <summary>在 SSE 行分配前限制整个原始正文；原始流仍由 HTTP 响应拥有。</summary>
internal sealed class VertexReadStream(Stream source, int maximum, long? expected) : Stream
{
    private long _read;
    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Check(source.Read(buffer, offset, count));
    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return Check(count);
    }
    /// <summary>检查实际字节总量和 EOF 声明长度，不以截断换取结束。</summary>
    private int Check(int count)
    {
        _read += count;
        if (_read > maximum || expected is { } size && (_read > size || count == 0 && _read != size)) throw new InvalidDataException("Vertex SSE 原始字节超限或声明长度不匹配。");
        return count;
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
