using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>验证默认头、发送前取消/到期和请求客户端的独立释放。</summary>
internal sealed class VertexHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal string? DefaultHeader { get; init; }
    internal Action? OnCreate { get; init; }
    internal Action? OnDispose { get; init; }
    internal bool Disposed { get; private set; }
    internal List<OllamaHttpClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new OllamaHttpClient(handler, OnDispose); if (DefaultHeader is not null) client.DefaultRequestHeaders.Add(DefaultHeader, DefaultHeader == "Authorization" ? "Bearer stale" : "stale");
        Names.Add(name); Clients.Add(client); OnCreate?.Invoke(); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
