namespace Asgard.Loki.Video.Ali.Tests;

/// <summary>观察供应商外层客户端释放，不连接实际网络。</summary>
internal sealed class AliTestHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    private bool _disposed;
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); if (disposing && !_disposed) { _disposed = true; onDispose(); }
    }
}
