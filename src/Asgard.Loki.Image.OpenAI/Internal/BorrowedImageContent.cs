using System.Net;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>一次性发送借用的图片/遮罩流，不通过长度查询、回卷或关闭流来管理调用者资源。</summary>
internal sealed class BorrowedImageContent(Stream source) : HttpContent
{
    private int _serialized;
    /// <summary>让 HTTP 传输自行选择 framing，不预读上传来计算长度。</summary>
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <summary>拒绝处理器触发的二次序列化，避免从文件尾部悄悄重放请求。</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _serialized, 1) != 0) throw new InvalidOperationException("图片上传不能隐式重复序列化。");
        await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
