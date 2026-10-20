namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>记录单次客户端释放，处理器由测试拥有。</summary>
internal sealed class AudioTestClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
