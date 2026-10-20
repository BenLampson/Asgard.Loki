using System.Net.Http.Headers;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>离线替代工厂及处理器，每次客户端由调用拥有，工厂仍由测试拥有。</summary>
internal sealed class AzureImageTestFactory : HttpMessageHandler, IHttpClientFactory
{
    internal int Creates { get; private set; }
    internal int Calls { get; private set; }
    internal int ClientDisposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal string? Name { get; private set; }
    internal Action? OnCreate { get; set; }
    internal Action? OnClientDispose { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
        = (_, _) => Task.FromResult(AzureImageFixture.Json(AzureImageFixture.Part("result")));
    public HttpClient CreateClient(string name)
    {
        Creates++; Name = name; OnCreate?.Invoke();
        var client = new AzureImageTestClient(this, () => { ClientDisposals++; OnClientDispose?.Invoke(); });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "unrelated-factory-secret");
        client.DefaultRequestHeaders.Add("api-key", "unrelated-factory-key"); return client;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return OnSend(request, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
