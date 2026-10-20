namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>释放每次调用的客户端，但保持借用工厂/处理器存活。</summary>
internal sealed class SunoTestClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
