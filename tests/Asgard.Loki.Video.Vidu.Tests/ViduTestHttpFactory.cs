namespace Asgard.Loki.Video.Vidu.Tests;

/// <summary>观察借用工厂、新建客户端及发送前取消边界。</summary>
internal sealed class ViduTestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int ClientsDisposed { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnCreate { get; init; }
    internal Action? OnDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new ViduTestHttpClient(handler, () => { ClientsDisposed++; OnDispose?.Invoke(); }); OnCreate?.Invoke(); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
