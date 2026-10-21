namespace Asgard.Loki.Rerank.SiliconFlow.Tests;

/// <summary>验证客户端名称和所有权，供应商与核心都不得释放调用方工厂。</summary>
internal sealed class SiliconFlowRerankTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>实际创建的请求级客户端名称。</summary>
    internal List<string> Names { get; } = [];
    /// <summary>请求级客户端释放次数。</summary>
    internal int ClientDisposals { get; private set; }
    /// <summary>借用的工厂是否被错误释放。</summary>
    internal bool Disposed { get; private set; }
    /// <summary>客户端释放时的可控回调。</summary>
    internal Action? OnClientDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        Names.Add(name); return new SiliconFlowRerankTestClient(handler, () => { ClientDisposals++; OnClientDispose?.Invoke(); });
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
