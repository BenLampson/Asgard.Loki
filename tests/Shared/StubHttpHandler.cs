namespace Asgard.Loki.Testing;

/// <summary>将 HTTP 调用替换为确定性的本地协议响应。</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int _calls;
    /// <summary>实际请求次数，用于检查是否发生隐式重试。</summary>
    internal int Calls => _calls;
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        cancellationToken.ThrowIfCancellationRequested();
        return send(request, cancellationToken);
    }
}
