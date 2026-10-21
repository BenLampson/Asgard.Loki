namespace Asgard.Loki.Embedding.Google.Tests;

/// <summary>跟踪借用工厂的名称和生命周期，底层替代处理器由测试调用者持有。</summary>
internal sealed class GoogleEmbeddingTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>创建的命名 HTTP 客户端。</summary>
    internal List<string> Names { get; } = [];
    /// <summary>是否被释放。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    { ObjectDisposedException.ThrowIf(Disposed, this); Names.Add(name); return new(handler, disposeHandler: false); }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
