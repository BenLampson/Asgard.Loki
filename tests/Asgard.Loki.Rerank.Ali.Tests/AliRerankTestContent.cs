using System.Net;
using System.Text;

namespace Asgard.Loki.Rerank.Ali.Tests;

/// <summary>观察响应释放、早期拒绝及正文读取期间的取消，不连接真实服务。</summary>
internal sealed class AliRerankTestContent(string json, bool waitForCancellation = false) : HttpContent
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(json);
    /// <summary>正文是否已读取。</summary>
    internal bool Read { get; private set; }
    /// <summary>内容是否已释放。</summary>
    internal bool Disposed { get; private set; }
    /// <summary>确定性同步正文已开始读取。</summary>
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Read = true; Started.TrySetResult();
        if (waitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
        await stream.WriteAsync(_bytes, cancellationToken);
    }
    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
