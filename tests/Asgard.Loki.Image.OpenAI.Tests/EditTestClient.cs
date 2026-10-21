namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>客户端释放和失败独立于响应流观测，不释放借用的处理器。</summary>
internal sealed class EditTestClient(HttpMessageHandler handler) : HttpClient(handler, disposeHandler: false)
{
    internal bool Disposed { get; private set; }
    internal bool FailDispose { get; init; }
    protected override void Dispose(bool disposing)
    { Disposed = true; base.Dispose(disposing); if (FailDispose) throw new IOException("fixture client disposal failure"); }
}
