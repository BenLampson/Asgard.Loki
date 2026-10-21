namespace Asgard.Loki.Video.Google.Tests;

/// <summary>在完整调用最终释放时注入故障或取消。</summary>
internal sealed class GoogleTestHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); if (disposing) onDispose();
    }
}
