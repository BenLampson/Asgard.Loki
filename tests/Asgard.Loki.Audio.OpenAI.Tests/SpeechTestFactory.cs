namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>只提供本地替代处理器，观察借用工厂与每次调用客户端的独立生命周期。</summary>
internal sealed class SpeechTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal List<SpeechTestHttpClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    internal bool Disposed { get; private set; }
    public HttpClient CreateClient(string name)
    {
        Names.Add(name); var client = new SpeechTestHttpClient(handler); Clients.Add(client); return client;
    }
    public void Dispose() => Disposed = true;
}
