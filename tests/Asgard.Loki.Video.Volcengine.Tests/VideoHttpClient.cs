namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>验证调用拥有的 HttpClient 在结果公开前释放，处理器仍由测试拥有。</summary>
internal sealed class VideoHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler,false)
{
    private bool _disposed;
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !_disposed) { _disposed = true; onDispose(); }
    }
}
