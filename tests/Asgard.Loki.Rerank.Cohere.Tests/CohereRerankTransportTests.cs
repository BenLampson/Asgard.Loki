using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Cohere.Tests;

/// <summary>协议错误、原生上游错误和正文终态必须明确失败，不能把部分结果转为成功。</summary>
public sealed class CohereRerankTransportTests
{
    /// <summary>原生 HTTP 错误保留完整状态与原文，即使 Retry-After 存在也只发送一次。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(498)] [InlineData(499)] [InlineData(503)]
    public async Task HttpFailure_PreservesStatusAndBodyWithoutRetry(int status)
    {
        const string raw = " \n{\"message\":\"failed 原文😀\",\"id\":\"native-id\",\"future\":false}\n ";
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(raw) };
            response.Headers.Add("Retry-After", "1"); return Task.FromResult(response);
        });
        await using var client = CohereRerankFixtures.Build(CohereRerankFixtures.Adapter(handler));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => client.Rerank.RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>HTTP 200 中的原生或兼容错误对象不变成空排名或默认分数。</summary>
    [Theory] [InlineData("{\"message\":\"failed\",\"id\":\"native\"}")]
    [InlineData("{\"error\":{\"message\":\"failed\"}}")]
    [InlineData("{\"error\":false,\"results\":[{\"index\":0,\"relevance_score\":0}]}")]
    public async Task ErrorEnvelope_IsNotSuccessfulRanking(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) }));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>非 JSON、截断正文或嵌套重复字段不能通过解析并伪装为完整原生结果。</summary>
    [Theory] [InlineData("not-json")] [InlineData("{\"results\":[")]
    [InlineData("{\"results\":[],\"results\":[]}")]
    [InlineData("{\"results\":[{\"index\":0,\"index\":1,\"relevance_score\":0}]}")]
    public async Task MalformedJson_IsRejected(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>意外 SSE 在读取正文前拒绝，不等待永不结束的流，也不合成成功终态。</summary>
    [Fact]
    public async Task UnexpectedSse_IsRejectedAtHeadersAndDisposed()
    {
        using var content = new TrackedRerankContent("data: {}\n\n"); content.Headers.ContentType = new("text/event-stream");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.False(content.Serialized); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>已知错误字段为 null 和未来同名 message 字段在合法排名中仍按原生保留。</summary>
    [Fact]
    public async Task NativeNullableExtensions_StayDistinctFromError()
    {
        var raw = CohereRerankFixtures.Read("v2-basic"); raw["error"] = null; raw["message"] = "future metadata";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(CohereRerankFixtures.Json(raw)));
        var result = await CohereRerankFixtures.Adapter(handler).RerankAsync(CohereRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("error").ValueKind); Assert.Equal("future metadata", result.Data.GetProperty("message").GetString());
    }

    /// <summary>原始分数精确处于单位区间时保留数值表示，包括合法负零及极小正数。</summary>
    [Theory] [InlineData("-0.0")] [InlineData("1.000")] [InlineData("10e-1")] [InlineData("1e-999")]
    public void NativeScore_ViewRetainsExactJson(string score)
    {
        using var source = JsonDocument.Parse("{\"index\":0,\"relevance_score\":" + score + "}");
        var item = new CohereRerankItem(source.RootElement); Assert.Equal(score, item.Data.GetProperty("relevance_score").GetRawText());
    }
}
