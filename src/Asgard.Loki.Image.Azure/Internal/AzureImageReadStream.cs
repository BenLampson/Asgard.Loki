namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>图像原生 SSE 的解码前字节上限/声明长度验证，不取得源流所有权。</summary>
internal sealed class AzureImageReadStream(Stream source, long? expected, int maximumBytes) : Stream
{
    private long _read;
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Count(source.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    /// <summary>越界块不交给解码器，正常 EOF 才能证实正文没有声明长度缺口。</summary>
    private int Count(int bytes)
    {
        if (bytes > maximumBytes - _read || expected is { } size && bytes > size - _read)
            throw new InvalidDataException("Azure 图像 SSE 超过显式字节上限或声明长度。");
        _read += bytes;
        if (bytes == 0 && expected is { } length && _read != length) throw new InvalidDataException("Azure 图像 SSE 未达到声明长度。");
        return bytes;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
