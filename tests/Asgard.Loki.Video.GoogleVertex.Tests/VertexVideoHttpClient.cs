namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>在完整客户端释放时观察故障和最终取消。</summary>
internal sealed class VertexVideoHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
