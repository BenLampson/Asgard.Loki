namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>借用工厂与调用拥有客户端的独立生命周期探针。</summary>
internal sealed class VideoHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>可控客户端释放边界。</summary>
    internal Action? OnClientDispose { get; init; }
    /// <summary>实际客户端释放次数。</summary>
    internal int ClientsDisposed { get; private set; }
    /// <summary>工厂本身不应被供应商释放。</summary>
    internal int Disposals { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new VideoHttpClient(handler,() => { ClientsDisposed++; OnClientDispose?.Invoke(); });
    /// <inheritdoc />
    public void Dispose() => Disposals++;
}
