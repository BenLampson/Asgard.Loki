namespace Asgard.Loki.Image.GoogleVertex.Tests;

/// <summary>观察本次 HTTP 客户端释放，借用工厂/处理器继续存活。</summary>
internal sealed class VertexTestClient(HttpMessageHandler handler, Action dispose) : HttpClient(handler, false)
{
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) dispose(); }
}
