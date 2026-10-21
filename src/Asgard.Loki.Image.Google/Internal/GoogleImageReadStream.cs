namespace Asgard.Loki.Image.Google.Internal;

/// <summary>有界解码前原生 SSE 字节窗口，不读取源流 Length/Position 或接管释放权。</summary>
internal sealed class GoogleImageReadStream(Stream source, long? expected, int maximum) : Stream
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
    private int Count(int count)
    {
        if (count > maximum - _read || expected is { } declared && count > declared - _read) throw new InvalidDataException("Gemini图片 SSE 超过上限或声明长度。");
        _read += count;
        if (count == 0 && expected is { } length && length != _read) throw new InvalidDataException("Gemini图片 SSE 未达到声明长度。");
        return count;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
