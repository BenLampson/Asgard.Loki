using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>验证默认头和客户端释放，借用工厂本身不被模块释放。</summary>
internal sealed class AwsHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
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
        var client = new OllamaHttpClient(handler, OnDispose); if (DefaultHeader is not null) client.DefaultRequestHeaders.Add(DefaultHeader, DefaultHeader == "Authorization" ? "Bearer stale" : "stale"); Names.Add(name); Clients.Add(client); OnCreate?.Invoke(); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
