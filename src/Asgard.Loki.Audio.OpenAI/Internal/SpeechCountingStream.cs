namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>统计 SSE 解码前的原始字节并验证长度；源流由外层调用独立释放。</summary>
internal sealed class SpeechCountingStream(Stream source, long? expected) : Stream
{
    internal long BytesRead { get; private set; }
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
    /// <summary>过长正文在交付前失败；仅正常读取至 EOF 后校验不足长度。</summary>
    private int Count(int read)
    {
        BytesRead = checked(BytesRead + read);
        if (expected is { } length && (BytesRead > length || read == 0 && BytesRead != length))
            throw new InvalidDataException("SSE 原始正文字节数与 Content-Length 不一致。");
        return read;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
