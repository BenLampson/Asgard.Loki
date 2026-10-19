using System.Net.Http.Headers;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>借用工厂与每次新建客户端分开验证；不连接真实服务器。</summary>
internal sealed class OllamaHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    internal bool Disposed { get; private set; }
    internal string? DefaultBearer { get; init; }
    internal Action? OnClientDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new OllamaHttpClient(handler, OnClientDispose);
        if (DefaultBearer is { } key) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Clients.Add(client); Names.Add(name); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
