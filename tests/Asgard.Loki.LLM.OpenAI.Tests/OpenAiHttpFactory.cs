using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>检查调用拥有的客户端释放顺序，不释放借用工厂和处理器。</summary>
internal sealed class OpenAiHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    internal Action? OnDispose { get; init; }
    internal List<OllamaHttpClient> Clients { get; } = [];
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new OllamaHttpClient(handler, OnDispose); Clients.Add(client); return client;
    }
}
