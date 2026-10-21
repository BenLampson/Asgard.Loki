namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>可观测借用工厂、参数冻结时点和单次客户端所有权。</summary>
internal sealed class AzureTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int Calls { get; private set; }
    internal int ClientDisposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnCreate { get; init; }
    internal Action? OnClientDispose { get; init; }
    public HttpClient CreateClient(string name)
    { Calls++; OnCreate?.Invoke(); return new AzureTestClient(handler, () => { ClientDisposals++; OnClientDispose?.Invoke(); }); }
    public void Dispose() => Disposed = true;
}
