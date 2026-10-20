namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>检查请求拥有的客户端是否在成功终态交付前释放，不释放借用处理器。</summary>
internal sealed class CohereHttpClient(HttpMessageHandler handler, Action? onDispose) : HttpClient(handler, false)
{
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !Disposed) { Disposed = true; onDispose?.Invoke(); }
    }
}
