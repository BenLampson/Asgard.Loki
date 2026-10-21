using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Rerank.Jina.Tests;

/// <summary>原生错误、截断/重复 JSON 和非预期流不能变成完整排名成功。</summary>
public sealed class JinaRerankTransportTests
{
    /// <summary>HTTP 原生错误保留完整正文、状态和请求标识，不因 Retry-After 自动重发。</summary>
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(422)] [InlineData(429)] [InlineData(503)]
    public async Task HttpFailure_PreservesNativeErrorWithoutRetry(int status)
    {
        const string raw = " \n{\"detail\":\"failed 原文😀\",\"code\":\"INPUT_MODEL_NOT_FOUND\",\"request_id\":\"native-id\",\"errors\":null,\"future\":false}\n ";
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(raw) };
            response.Headers.Add("Retry-After", "1"); return Task.FromResult(response);
        });
        await using var client = JinaRerankFixtures.Build(JinaRerankFixtures.Adapter(handler));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => client.Rerank.RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>HTTP 200 的原生或兼容错误对象仍明确失败，不能补造排名或用量。</summary>
    [Theory] [InlineData("{\"detail\":\"failed\",\"code\":\"INPUT_INVALID\",\"request_id\":\"id\"}")]
    [InlineData("{\"error\":{\"message\":\"failed\"}}")]
    [InlineData("{\"error\":false,\"results\":[]}")]
    public async Task BusinessError_IsNotSuccessfulRanking(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) }));
        var error = await Assert.ThrowsAsync<RerankUpstreamException>(() => JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(200, error.StatusCode); Assert.Equal(raw, error.ResponseBody); Assert.Equal(1, handler.Calls);
    }

    /// <summary>非 JSON、截断及嵌套重复键均拒绝，不用最后一个同名字段掩盖关联错误。</summary>
    [Theory] [InlineData("not-json")] [InlineData("{\"results\":[")]
    [InlineData("{\"results\":[],\"results\":[]}")]
    [InlineData("{\"results\":[{\"index\":0,\"index\":1,\"relevance_score\":0}]}")]
    public async Task MalformedJson_IsRejected(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>成功 SSE 在响应头阶段拒绝，不读取可能永不结束的事件流。</summary>
    [Fact]
    public async Task UnexpectedSse_IsRejectedBeforeBodyRead()
    {
        using var content = new TrackedRerankContent("data: {}\n\n"); content.Headers.ContentType = new("text/event-stream");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.False(content.Serialized); Assert.True(content.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>已知错误字段为 null 或未来 detail 元数据不能过滤掉完整有效响应。</summary>
    [Fact]
    public async Task FutureFields_InValidResponseRemainNative()
    {
        var raw = JinaRerankFixtures.Read("text"); raw["error"] = null; raw["detail"] = new System.Text.Json.Nodes.JsonObject { ["future"] = false };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JinaRerankFixtures.Json(raw)));
        var result = await JinaRerankFixtures.Adapter(handler).RerankAsync(JinaRerankFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("error").ValueKind); Assert.False(result.Data.GetProperty("detail").GetProperty("future").GetBoolean());
    }
}
