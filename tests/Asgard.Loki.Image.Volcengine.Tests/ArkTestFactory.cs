namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>离线 HTTP 工厂，记录单次交换及资源边界。</summary>
internal sealed class ArkTestFactory : HttpMessageHandler, IHttpClientFactory
{
    internal int Calls { get; private set; }
    internal int Creates { get; private set; }
    internal int Disposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnCreate { get; set; }
    internal Action? OnDisposeClient { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
        = (_, _) => Task.FromResult(ArkFixture.Json(ArkFixture.Part("success")));
    public HttpClient CreateClient(string name)
    { Creates++; OnCreate?.Invoke(); return new ArkTestClient(this, () => { Disposals++; OnDisposeClient?.Invoke(); }); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    { Calls++; return OnSend(message, token); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
