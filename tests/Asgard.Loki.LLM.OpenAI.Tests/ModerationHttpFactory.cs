namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>离线 HTTP 工厂，记录单次交换及资源边界。</summary>
internal sealed class ModerationHttpFactory : HttpMessageHandler, IHttpClientFactory
{
    internal int Calls { get; private set; }
    internal int Creates { get; private set; }
    internal int Disposals { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action? OnCreate { get; set; }
    internal Action? OnDisposeClient { get; set; }
    internal Action<HttpClient>? ConfigureClient { get; set; }
    internal string? ClientName { get; private set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
        = (_, _) => Task.FromResult(ModerationFixture.Json(ModerationFixture.Part("responses")[1]));
    public HttpClient CreateClient(string name)
    { Creates++; ClientName = name; OnCreate?.Invoke(); var client = new ModerationHttpClient(this, () => { Disposals++; OnDisposeClient?.Invoke(); }); ConfigureClient?.Invoke(client); return client; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    { Calls++; return OnSend(message, token); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
