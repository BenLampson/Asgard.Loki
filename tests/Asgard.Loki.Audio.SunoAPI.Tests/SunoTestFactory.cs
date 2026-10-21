namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>离线 HTTP 工厂与处理器，观察请求次数和资源释放，不能接触网络。</summary>
internal sealed class SunoTestFactory : HttpMessageHandler, IHttpClientFactory
{
    internal int Calls { get; private set; }
    internal int Creates { get; private set; }
    internal int ClientDisposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal string? LastClientName { get; private set; }
    internal Action? OnCreate { get; set; }
    internal Action? OnClientDispose { get; set; }
    internal System.Net.Http.Headers.AuthenticationHeaderValue? DefaultAuthorization { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
        = (_, _) => Task.FromResult(SunoFixture.Json(SunoFixture.Part("submit").GetProperty("music")));
    public HttpClient CreateClient(string name)
    {
        Creates++; LastClientName = name; OnCreate?.Invoke();
        var client = new SunoTestClient(this, () => { ClientDisposals++; OnClientDispose?.Invoke(); });
        client.DefaultRequestHeaders.Authorization = DefaultAuthorization; return client;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return OnSend(request, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
