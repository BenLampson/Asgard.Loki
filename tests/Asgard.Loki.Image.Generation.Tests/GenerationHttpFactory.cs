namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>记录命名客户端，复用离线处理器，不持有任何真实网络或凭据。</summary>
internal sealed class GenerationHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private int _calls;
    internal int Calls => _calls;
    internal System.Collections.Concurrent.ConcurrentBag<string> Names { get; } = [];
    public HttpClient CreateClient(string name)
    {
        Interlocked.Increment(ref _calls); Names.Add(name);
        var client = new HttpClient(handler, disposeHandler: false);
        if (name == "Asgard.Loki.Image.Content")
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer inherited-not-for-download");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Application-Secret", "inherited-not-for-download");
        }
        return client;
    }
}
