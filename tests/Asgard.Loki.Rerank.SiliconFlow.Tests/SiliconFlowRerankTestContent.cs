using System.Net;
using System.Text;

namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>可确定控制读取、尾部和释放的离线响应正文，不依赖计时或真实网络。</summary>
internal sealed class SiliconFlowRerankTestContent(string body) : HttpContent
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);
    /// <summary>读取开始通知，用于精确取消正文读取。</summary>
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>正文读取调用次数。</summary>
    internal int ReadCalls { get; private set; }
    /// <summary>响应拥有的正文已释放。</summary>
    internal bool Disposed { get; private set; }
    /// <summary>正文读取等待调用方取消。</summary>
    internal bool WaitForCancellation { get; init; }
    /// <summary>写完完整 JSON 后仍返回读取错误。</summary>
    internal bool ThrowAfterBody { get; init; }
    /// <summary>写完正文后的同步回调。</summary>
    internal Action? AfterBody { get; init; }
    /// <summary>释放时的回调，用于取消或释放失败。</summary>
    internal Action? OnDispose { get; init; }
    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        ReadCalls++; Started.TrySetResult();
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
        await stream.WriteAsync(_bytes, cancellationToken); AfterBody?.Invoke();
        if (ThrowAfterBody) throw new IOException("native body tail failed");
    }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (Disposed) return;
        Disposed = true; base.Dispose(disposing); if (disposing) OnDispose?.Invoke();
    }
}
