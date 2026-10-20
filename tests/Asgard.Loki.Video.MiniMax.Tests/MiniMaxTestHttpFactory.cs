namespace Asgard.Loki.Video.MiniMax.Tests;

/// <summary>观察借用工厂与新建调用客户端各自的所有权。</summary>
internal sealed class MiniMaxTestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int ClientDisposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnClientDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new MiniMaxTestHttpClient(handler, () => { ClientDisposals++; OnClientDispose?.Invoke(); });
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
