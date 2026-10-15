namespace Asgard.Loki.Testing;

/// <summary>为供应商测试提供不连接网络的 HTTP 工厂。</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
