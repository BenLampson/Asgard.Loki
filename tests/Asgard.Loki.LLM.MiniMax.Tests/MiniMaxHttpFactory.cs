using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.MiniMax.Tests;

/// <summary>客户端归单次调用拥有，工厂和处理器仍由测试调用者借出。</summary>
internal sealed class MiniMaxHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal Action<HttpClient>? Configure { get; init; }
    internal Action? OnDispose { get; init; }
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal bool Disposed { get; private set; }
    public HttpClient CreateClient(string name) { var client = new OllamaHttpClient(handler, OnDispose); Clients.Add(client); Configure?.Invoke(client); return client; }
    public void Dispose() => Disposed = true;
}
