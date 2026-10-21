namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>只跟踪适配器创建的请求级客户端，不接管借用的处理器。</summary>
internal sealed class SiliconFlowRerankTestClient(HttpMessageHandler handler, Action released) : HttpClient(handler, disposeHandler: false)
{
    private bool _disposed;
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        try { base.Dispose(disposing); } finally { if (disposing) released(); }
    }
}
