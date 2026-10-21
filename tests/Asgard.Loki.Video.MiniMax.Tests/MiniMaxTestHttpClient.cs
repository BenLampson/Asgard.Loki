namespace Asgard.Loki.Video.MiniMax.Tests;

/// <summary>完整客户端释放的故障与取消探针。</summary>
internal sealed class MiniMaxTestHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
