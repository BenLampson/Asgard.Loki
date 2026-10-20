namespace Asgard.Loki.Image.Ali.Tests;

/// <summary>离线 HTTP 工厂，记录单次交换及资源边界。</summary>
internal sealed class AliTestFactory : HttpMessageHandler, IHttpClientFactory
{
    internal int Calls { get; private set; }
    internal int Creates { get; private set; }
    internal int Disposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnCreate { get; set; }
    internal Action? OnDisposeClient { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
        = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Part("success")));
    public HttpClient CreateClient(string name)
    { Creates++; OnCreate?.Invoke(); return new AliTestClient(this, () => { Disposals++; OnDisposeClient?.Invoke(); }); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    { Calls++; return OnSend(message, token); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
