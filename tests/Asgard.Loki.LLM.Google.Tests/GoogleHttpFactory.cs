using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>记录当前调用拥有的客户端，借用处理器不随客户端关闭。</summary>
internal sealed class GoogleHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal Action<HttpClient>? Configure { get; init; }
    internal Action? OnDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name) { var client = new OllamaHttpClient(handler, OnDispose); Clients.Add(client); Configure?.Invoke(client); return client; }
}
