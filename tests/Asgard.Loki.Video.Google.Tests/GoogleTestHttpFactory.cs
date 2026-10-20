namespace Asgard.Loki.Video.Google.Tests;

/// <summary>观察新建客户端的释放，不把调用者拥有的工厂交给 SDK 释放。</summary>
internal sealed class GoogleTestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int ClientDisposals { get; private set; }
    internal int Disposals { get; private set; }
    internal Action? OnClientDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new GoogleTestHttpClient(handler, () => { ClientDisposals++; OnClientDispose?.Invoke(); });
    /// <inheritdoc />
    public void Dispose() => Disposals++;
}
