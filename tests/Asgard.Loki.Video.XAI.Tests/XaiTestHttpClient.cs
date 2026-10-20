namespace Asgard.Loki.Video.XAI.Tests;

/// <summary>在最终客户端释放时注入故障或取消。</summary>
internal sealed class XaiTestHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
