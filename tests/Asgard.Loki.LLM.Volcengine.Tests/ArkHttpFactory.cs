using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>拥有的单次客户端与借用工厂分别观察，不连接真实服务。</summary>
internal sealed class ArkHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal bool Disposed { get; private set; }
    internal Action<HttpClient>? Configure { get; init; }
    internal Action? OnDispose { get; init; }
    public HttpClient CreateClient(string name)
    { var client = new OllamaHttpClient(handler, OnDispose); Clients.Add(client); Configure?.Invoke(client); return client; }
    public void Dispose() => Disposed = true;
}
