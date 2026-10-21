namespace Asgard.Loki.Rerank.Cohere.Tests;

/// <summary>验证供应商不释放借用工厂及外部底层处理器。</summary>
internal sealed class BorrowedRerankFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>本次创建的客户端名称。</summary>
    internal List<string> Names { get; } = [];
    /// <summary>工厂是否已由调用者释放。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    { ObjectDisposedException.ThrowIf(Disposed, this); Names.Add(name); return new(handler, disposeHandler: false); }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
