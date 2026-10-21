namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>观察工厂借用、连接前快照与客户端最终释放。</summary>
internal sealed class AudioTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int Calls { get; private set; }
    internal int ClientDisposals { get; private set; }
    internal int Disposals { get; private set; }
    internal Action? OnCreate { get; init; }
    internal Action? OnClientDispose { get; init; }
    public HttpClient CreateClient(string name)
    { Calls++; OnCreate?.Invoke(); return new AudioTestClient(handler, () => { ClientDisposals++; OnClientDispose?.Invoke(); }); }
    public void Dispose() => Disposals++;
}
