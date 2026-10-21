using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>记录借用工厂与每次拥有的客户端，提供确定性的资源结束回调。</summary>
internal sealed class SearchFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    internal Action<HttpClient>? Configure { get; init; }
    internal Action? OnDispose { get; init; }
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    { Names.Add(name); var client = new OllamaHttpClient(handler, OnDispose); Clients.Add(client); Configure?.Invoke(client); return client; }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
