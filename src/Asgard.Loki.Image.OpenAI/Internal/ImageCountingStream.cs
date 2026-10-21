namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>按解码前原始字节校验 SSE 长度，不接管源流的生命周期或查询其长度。</summary>
internal sealed class ImageCountingStream(Stream source, long? expected) : Stream
{
    private long _bytes;
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
    /// <summary>只在正常 EOF 校验不足长度，过长则在交付前失败。</summary>
    private int Count(int read)
    {
        _bytes = checked(_bytes + read);
        if (expected is { } length && (_bytes > length || read == 0 && _bytes != length)) throw new InvalidDataException("图像 SSE 长度与 Content-Length 不符。");
        return read;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
