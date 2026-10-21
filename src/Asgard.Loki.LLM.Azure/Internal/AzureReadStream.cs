namespace Asgard.Loki.LLM.Azure.Internal;

/// <summary>在 SSE 解析器分配行之前限制原始总字节，原始流仍由 HTTP 响应拥有。</summary>
internal sealed class AzureReadStream(Stream source, int maximum, long? expected) : Stream
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
    /// <summary>同时验证实际读取上限与 EOF 声明长度。</summary>
    private int Check(int count)
    {
        _read += count;
        if (_read > maximum || expected is { } size && (_read > size || count == 0 && _read != size)) throw new InvalidDataException("Azure SSE 超过原始字节上限或声明长度不匹配。");
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
