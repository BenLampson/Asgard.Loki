namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>借用工厂及独立新客户端的生命周期替身。</summary>
internal sealed class VertexVideoHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int ClientsCreated { get; private set; }
    internal int ClientsDisposed { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action<HttpClient>? OnCreate { get; init; }
    internal Action? OnDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new VertexVideoHttpClient(handler, () => { ClientsDisposed++; OnDispose?.Invoke(); }); ClientsCreated++; OnCreate?.Invoke(client); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
