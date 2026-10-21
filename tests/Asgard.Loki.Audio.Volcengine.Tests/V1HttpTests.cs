using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>HTTP query 保真、关联和资源终态，以替代处理器离线验证。</summary>
public sealed class V1HttpTests
{
    /// <summary>完整请求和结果原生字段保留，不把前端 JSON 字符串改为对象。</summary>
    [Fact]
    public async Task NativeHttpThroughPublicClient()
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://openspeech.bytedance.com/api/v1/tts", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer;fixture-header-access", Assert.Single(message.Headers.GetValues("Authorization")));
            Assert.Equal(HttpMethod.Post, message.Method); Assert.False(message.Headers.Contains("X-Api-Key"));
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(V1Fixture.Root.GetProperty("requests")[1].GetProperty("wire_request"), body.RootElement));
            return Response(V1Fixture.Root.GetProperty("http_response").GetRawText());
        });
        var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(VolcengineV1SpeechTransport.Http), new StubHttpClientFactory(handler));
        await using var client = new LokiClientBuilder().AddAudio(new AudioOptions { SpeechRoutes = [new SpeechRoute { Model = "sdk-public-route", Targets = [adapter] }] }).Build();
        var result = await client.Audio.Speech.GenerateNativeAsync(V1Fixture.Request(1), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result.Audio.ToArray());
        var frame = Assert.Single(result.Frames); Assert.True(JsonElement.DeepEquals(V1Fixture.Root.GetProperty("http_response"), frame));
        Assert.Equal(JsonValueKind.String, frame.GetProperty("addition").GetProperty("frontend").ValueKind); Assert.Equal(1, handler.Calls);
    }
    /// <summary>不同 HTTP 或业务错误保留原文，不重试或回退。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeHttpErrors(bool statusFailure)
    {
        var text = statusFailure ? "non-json native error" : V1Fixture.Root.GetProperty("http_error").GetRawText();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Response(text, statusFailure ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)));
        var error = await Assert.ThrowsAsync<VolcengineV1SpeechException>(() => V1Fixture.Collect(Adapter(handler).StreamNativeAsync(V1Fixture.Request(1), TestContext.Current.CancellationToken)));
        Assert.Equal(statusFailure ? null : (long?)3001, error.NativeCode); Assert.Equal(Encoding.UTF8.GetBytes(text), error.RawResponse.ToArray());
        Assert.Equal(statusFailure ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, error.StatusCode); Assert.Equal(1, handler.Calls);
    }
    /// <summary>错误关联、缺失终态、重复字段或损坏音频不能发布结果。</summary>
    [Theory]
    [InlineData("reqid")]
    [InlineData("sequence-zero")]
    [InlineData("sequence-missing")]
    [InlineData("sequence-type")]
    [InlineData("code")]
    [InlineData("base64")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("message-bound")]
    [InlineData("decoded-bound")]
    [InlineData("duplicate")]
    [InlineData("unicode")]
    [InlineData("truncated")]
    public async Task InvalidHttp(string kind)
    {
        var root = JsonNode.Parse(V1Fixture.Root.GetProperty("http_response").GetRawText())!.AsObject();
        switch (kind)
        {
            case "reqid": root["reqid"] = "other"; break;
            case "sequence-zero": root["sequence"] = 0; break;
            case "sequence-missing": root.Remove("sequence"); break;
            case "sequence-type": root["sequence"] = "-1"; break;
            case "code": root.Remove("code"); break;
            case "base64": root["data"] = "!?"; break;
            case "empty": root["data"] = ""; break;
            case "null": root["data"] = null; break;
        }
        var json = kind switch
        {
            "duplicate" => root.ToJsonString()[..^1] + ",\"code\":3000}",
            "unicode" => root.ToJsonString()[..^1] + ",\"unknown\":\"\\uD800\"}",
            "truncated" => root.ToJsonString()[..^1],
            _ => root.ToJsonString()
        };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Response(json)));
        var adapter = new VolcengineV1SpeechAdapter(V1Fixture.Options(VolcengineV1SpeechTransport.Http,
            message: kind == "message-bound" ? 20 : 4096, decoded: kind == "decoded-bound" ? 2 : 4096), new StubHttpClientFactory(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => V1Fixture.Collect(adapter.StreamNativeAsync(V1Fixture.Request(1), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>外层资源释放错误或最后一刻取消不发布成功。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeBoundary(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var disposed = false;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new V1Content(Encoding.UTF8.GetBytes(V1Fixture.Root.GetProperty("http_response").GetRawText()), () =>
            { disposed = true; if (cancel) cancellation.Cancel(); else throw new IOException("response dispose"); }) }));
        var chunks = new List<SpeechChunk>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var chunk in Adapter(handler).StreamNativeAsync(V1Fixture.Request(1), cancellation.Token)) chunks.Add(chunk);
        });
        Assert.Empty(chunks); Assert.True(disposed); Assert.Equal(1, handler.Calls);
    }
    private static VolcengineV1SpeechAdapter Adapter(StubHttpHandler handler)
        => new(V1Fixture.Options(VolcengineV1SpeechTransport.Http), new StubHttpClientFactory(handler));
    private static HttpResponseMessage Response(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
