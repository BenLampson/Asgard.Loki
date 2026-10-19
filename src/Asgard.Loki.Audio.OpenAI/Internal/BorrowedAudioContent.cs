using System.Net;

namespace Asgard.Loki.Audio.OpenAI.Internal;

/// <summary>仅发送一次借用上传流，不查询长度、不回卷，释放 HTTP 内容时不关闭调用者流。</summary>
internal sealed class BorrowedAudioContent(Stream source) : HttpContent
{
    private int _serialized;
    /// <summary>流可能不可 seek；长度由 HTTP 传输处理，不通过预读或缓存来计算。</summary>
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <summary>即使外部处理器要求重发，也不能隐式第二次读取原始上传。</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _serialized, 1) != 0) throw new InvalidOperationException("文件上传不允许隐式重复序列化或重放。");
        await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
