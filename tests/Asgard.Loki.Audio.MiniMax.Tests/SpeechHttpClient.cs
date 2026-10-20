namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>记录单次创建客户端的释放；处理器始终借用。</summary>
internal sealed class SpeechHttpClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
