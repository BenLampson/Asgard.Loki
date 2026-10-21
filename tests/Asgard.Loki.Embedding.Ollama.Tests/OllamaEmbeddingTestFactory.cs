namespace Asgard.Loki.Embedding.Ollama.Tests;

/// <summary>跟踪借用 HTTP 工厂，不创建真实网络连接。</summary>
internal sealed class OllamaEmbeddingTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>请求的客户端名称。</summary>
    internal List<string> Names { get; } = [];
    /// <summary>调用者工厂是否被释放。</summary>
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    { ObjectDisposedException.ThrowIf(Disposed, this); Names.Add(name); return new(handler, disposeHandler: false); }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
