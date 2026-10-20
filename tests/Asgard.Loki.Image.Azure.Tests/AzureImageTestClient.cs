namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>观察本次 HTTP 客户端释放，处理器继续由测试持有。</summary>
internal sealed class AzureImageTestClient(HttpMessageHandler handler, Action onDispose) : HttpClient(handler, false)
{
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) onDispose(); }
}
