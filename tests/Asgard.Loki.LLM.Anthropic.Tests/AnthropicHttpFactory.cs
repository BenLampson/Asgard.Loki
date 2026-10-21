using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>独立记录调用拥有的客户端及借用默认配置，不释放测试处理器。</summary>
internal sealed class AnthropicHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    internal Action<HttpClient>? Defaults { get; init; }
    internal Action? OnDispose { get; init; }
    internal List<OllamaHttpClient> Clients { get; } = [];
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new OllamaHttpClient(handler, OnDispose); Clients.Add(client); Defaults?.Invoke(client); return client;
    }
}
