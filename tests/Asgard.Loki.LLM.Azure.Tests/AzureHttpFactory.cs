using System.Net.Http.Headers;
using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>每个请求创建独立客户端，检查默认头隔离与借用工厂/源的所有权。</summary>
internal sealed class AzureHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal string? DefaultBearer { get; init; }
    internal string? DefaultApiKey { get; init; }
    internal Action? OnCreate { get; init; }
    internal Action? OnDispose { get; init; }
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    internal bool Disposed { get; private set; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new OllamaHttpClient(handler, OnDispose);
        if (DefaultBearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DefaultBearer);
        if (DefaultApiKey is not null) client.DefaultRequestHeaders.Add("api-key", DefaultApiKey);
        Clients.Add(client); Names.Add(name); OnCreate?.Invoke(); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
