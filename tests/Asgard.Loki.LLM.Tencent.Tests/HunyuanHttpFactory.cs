namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>离线 HTTP 工厂，记录单次交换及资源边界。</summary>
internal sealed class HunyuanHttpFactory : HttpMessageHandler, IHttpClientFactory
{
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);
    private int _creates;
    internal int Creates => Volatile.Read(ref _creates);
    private int _disposals;
    internal int Disposals => Volatile.Read(ref _disposals);
    internal bool Disposed { get; private set; }
    internal Action? OnCreate { get; set; }
    internal Action? OnDisposeClient { get; set; }
    internal Action<HttpClient>? ConfigureClient { get; set; }
    internal string? ClientName { get; private set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
        = (_, _) => Task.FromResult(HunyuanFixture.Reply(HunyuanFixture.Part("response")));
    public HttpClient CreateClient(string name)
    { Interlocked.Increment(ref _creates); ClientName = name; OnCreate?.Invoke(); var client = new HunyuanHttpClient(this, () => { Interlocked.Increment(ref _disposals); OnDisposeClient?.Invoke(); }); ConfigureClient?.Invoke(client); return client; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    { Interlocked.Increment(ref _calls); return OnSend(message, token); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
