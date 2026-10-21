namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>记录调用拥有的客户端释放，处理器仍由测试持有。</summary>
internal sealed class AzureTestClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
