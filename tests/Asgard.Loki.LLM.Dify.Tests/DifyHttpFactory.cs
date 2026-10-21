using System.Net.Http.Headers;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>借用工厂与每次新建客户端分开验证；不连接真实服务器。</summary>
internal sealed class DifyHttpFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal List<DifyHttpClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    internal bool Disposed { get; private set; }
    internal Action<HttpClient>? Configure { get; init; }
    internal string? DefaultBearer { get; init; }
    internal Action? OnClientDispose { get; init; }
    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        var client = new DifyHttpClient(handler, OnClientDispose);
        if (DefaultBearer is { } key) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Configure?.Invoke(client); Clients.Add(client); Names.Add(name); return client;
    }
    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
