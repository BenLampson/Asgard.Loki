namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>验证供应商只创建本次 HTTP 客户端，不释放借用工厂或底层处理器。</summary>
internal sealed class BorrowedEmbeddingFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>已创建的客户端名称。</summary>
    internal List<string> Names { get; } = [];
    /// <summary>工厂的释放状态。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        ObjectDisposedException.ThrowIf(Disposed, this); Names.Add(name); return new(handler, disposeHandler: false);
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
