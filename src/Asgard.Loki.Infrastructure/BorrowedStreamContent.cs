using System.Buffers;
using System.Net;

namespace Asgard.Loki.Infrastructure;

/// <summary>一次性原始流上传内容，借用输入流且不查询长度、位置、回卷或取得释放权。</summary>
public sealed class BorrowedStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _maximumBytes;
    private int _serialized;
    /// <summary>只验证流可读，正文从调用者当前位置读取；释放此内容不关闭输入流。</summary>
    public BorrowedStreamContent(Stream source) : this(source, long.MaxValue) { }
    /// <summary>限制本次剩余上传字节；超限不写出越界块，源流可能已消费一个读取块且不会回卷。</summary>
    public BorrowedStreamContent(Stream source, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!source.CanRead) throw new ArgumentException("上传源流必须可读。", nameof(source)); _source = source; _maximumBytes = maximumBytes;
    }
    /// <summary>不预读或回卷来计算长度，让 HTTP 传输选择 framing。</summary>
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <summary>即使外部处理器要求重发，也拒绝隐式二次读取原始输入。</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _serialized, 1) != 0) throw new InvalidOperationException("上传内容不允许隐式重复序列化或重放。");
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int count; long total = 0;
            while ((count = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (count > _maximumBytes - total) throw new InvalidDataException("上传正文超过显式字节上限。");
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                total += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
