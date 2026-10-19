using System.Net;
using System.Text;

namespace Asgard.Loki.Rerank.Jina.Tests;

/// <summary>跟踪响应读取和释放；可在正文读取时执行确定性取消或等待测试。</summary>
internal sealed class TrackedRerankContent(string json, Func<Stream, CancellationToken, Task>? write = null) : HttpContent
{
    /// <summary>是否已经读取过正文。</summary>
    internal bool Serialized { get; private set; }
    /// <summary>是否释放响应正文。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    { Serialized = true; return write is null ? stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken).AsTask() : write(stream, cancellationToken); }
    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
