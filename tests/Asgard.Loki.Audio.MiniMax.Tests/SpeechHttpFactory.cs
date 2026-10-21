namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>可观测工厂借用、客户端所有权及连接前参数快照。</summary>
internal sealed class SpeechHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal int Disposals { get; private set; }
    internal int ClientDisposals { get; private set; }
    internal int Calls { get; private set; }
    internal Action? OnCreate { get; init; }
    internal Action? OnClientDispose { get; init; }
    public HttpClient CreateClient(string name)
    { Calls++; OnCreate?.Invoke(); return new SpeechHttpClient(handler, () => { ClientDisposals++; OnClientDispose?.Invoke(); }); }
    public void Dispose() => Disposals++;
}
