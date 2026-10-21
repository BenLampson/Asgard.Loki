using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>访问真实服务器，同时验证最终请求并限制本次运行的请求数量。</summary>
internal sealed class LiveHttpHandler(string apiKey) : DelegatingHandler(new HttpClientHandler())
{
    private int _calls;
    /// <summary>只保存合成测试请求正文与状态，绝不保存认证头。</summary>
    internal List<(string Path, JsonElement Body, int Status)> Requests { get; } = [];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) > LiveConfiguration.MaximumRequests)
            throw new InvalidOperationException("Live-test request budget exceeded; request was not sent.");
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.True(request.Headers.Authorization?.Parameter == apiKey, "The configured test credential was not used.");
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var body = document.RootElement.Clone();
        LiveRequestBudget.Charge();
        var response = await base.SendAsync(request, cancellationToken);
        Requests.Add((request.RequestUri.AbsolutePath, body, (int)response.StatusCode));
        return response;
    }
}
