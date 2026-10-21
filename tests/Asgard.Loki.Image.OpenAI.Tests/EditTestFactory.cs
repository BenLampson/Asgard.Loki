namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>仅返回本地替代处理器；记录工厂调用和每次客户端的资源状态。</summary>
internal sealed class EditTestFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
{
    internal List<EditTestClient> Clients { get; } = [];
    internal List<string> Names { get; } = [];
    internal Action? OnCreate { get; init; }
    internal bool FailClientDispose { get; init; }
    internal bool Disposed { get; private set; }
    public HttpClient CreateClient(string name)
    { OnCreate?.Invoke(); Names.Add(name); var client = new EditTestClient(handler) { FailDispose = FailClientDispose }; Clients.Add(client); return client; }
    public void Dispose() => Disposed = true;
}
