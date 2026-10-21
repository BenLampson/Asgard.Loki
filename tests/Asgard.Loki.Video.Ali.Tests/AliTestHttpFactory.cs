namespace Asgard.Loki.Video.Ali.Tests;

/// <summary>验证工厂借用、调用客户端拥有，以及最终客户端释放边界。</summary>
internal sealed class AliTestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    /// <summary>客户端释放时的异常或取消探针。</summary>
    internal Action? OnClientDispose { get; init; }
    /// <summary>独立记录客户端释放次数。</summary>
    internal int ClientsDisposed { get; private set; }
    /// <summary>工厂不应由适配器释放。</summary>
    internal int Disposals { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new AliTestHttpClient(handler, () => { ClientsDisposed++; OnClientDispose?.Invoke(); });
    /// <inheritdoc />
    public void Dispose() => Disposals++;
}
