using Asgard.Loki.Audio;
using Asgard.Loki.Audio.Volcengine;
using Asgard.Loki.Testing;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Audio.Volcengine.Tests;

/// <summary>以本地分块 HTTP 响应验证火山语音协议。</summary>
public sealed class VolcengineSpeechTests
{
    /// <summary>认证、资源与音频参数映射正确，并保留音频、字幕、用量。</summary>
    [Fact]
    public async Task GenerateAsync_MapsAuthenticationAndDecodesFrames()
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("speech-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Equal("resource", Assert.Single(request.Headers.GetValues("X-Api-Resource-Id")));
            Assert.False(request.Headers.Contains("Authorization"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var parameters = body.RootElement.GetProperty("req_params");
            Assert.Equal("你好", parameters.GetProperty("text").GetString());
            Assert.Equal("speaker", parameters.GetProperty("speaker").GetString());
            Assert.Equal(24000, parameters.GetProperty("audio_params").GetProperty("sample_rate").GetInt32());
            Assert.Equal("kept", parameters.GetProperty("future").GetString());
            return Response("{\"code\":0,\"data\":\"AQI=\",\"sentence\":{\"text\":\"你好\"}}\n"
                + "{\"code\":0,\"data\":\"Aw==\"}\n{\"code\":20000000,\"data\":null,\"usage\":{\"text_words\":2}}");
        });
        var adapter = Adapter(handler);
        await using var client = new LokiClientBuilder().AddAudio(new AudioOptions
        { SpeechRoutes = [new SpeechRoute { Model = "voice", Targets = [adapter] }] }).Build();
        var result = await client.Audio.Speech.GenerateAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio.ToArray());
        Assert.Equal("你好", result.Frames[0].GetProperty("sentence").GetProperty("text").GetString());
        Assert.Equal(2, result.Frames[2].GetProperty("usage").GetProperty("text_words").GetInt32());
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>旧版认证不混入新版 API Key。</summary>
    [Fact]
    public async Task Streaming_SupportsExplicitLegacyAuthentication()
    {
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.False(request.Headers.Contains("X-Api-Key"));
            Assert.Equal("app", Assert.Single(request.Headers.GetValues("X-Api-App-Id")));
            Assert.Equal("token", Assert.Single(request.Headers.GetValues("X-Api-Access-Key")));
            return Task.FromResult(Response("{\"code\":20000000,\"data\":null}"));
        });
        var adapter = new VolcengineSpeechAdapter(new StubHttpClientFactory(handler), new VolcengineSpeechOptions
        { ResourceId = "resource", AppId = "app", AccessKey = "token" });
        var chunks = await CollectAsync(adapter.StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(chunks).IsCompleted);
    }

    /// <summary>业务错误不会被 HTTP 200 掩盖，也不会重试。</summary>
    [Fact]
    public async Task Streaming_PropagatesBusinessErrorWithoutRetry()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Response("{\"code\":45000000,\"message\":\"permission denied\",\"future\":1}")));
        var failure = await Assert.ThrowsAsync<SpeechUpstreamException>(() => CollectAsync(Adapter(handler).StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(45000000, failure.Code);
        Assert.Contains("future", failure.ResponseBody);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>无完成帧或无效音频编码明确失败。</summary>
    [Theory]
    [InlineData("{\"code\":0,\"data\":\"AQI=\"}")]
    [InlineData("{\"code\":0,\"data\":\"invalid-base64!\"}\n{\"code\":20000000}")]
    public async Task Streaming_RejectsIncompleteOrInvalidAudio(string body)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Response(body)));
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(Adapter(handler).StreamAsync(Request(), TestContext.Current.CancellationToken)));
    }

    /// <summary>认证方式冲突在网络调用前被拒绝。</summary>
    [Fact]
    public void Initialization_RejectsMixedAuthentication()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.Throws<ArgumentException>(() => new VolcengineSpeechAdapter(new StubHttpClientFactory(handler),
            new VolcengineSpeechOptions { ResourceId = "resource", ApiKey = "key", AppId = "app", AccessKey = "token" }));
    }
    /// <summary>创建新版认证适配器。</summary>
    private static VolcengineSpeechAdapter Adapter(StubHttpHandler handler) => new(new StubHttpClientFactory(handler),
        new VolcengineSpeechOptions { ResourceId = "resource", ApiKey = "speech-key" });
    /// <summary>创建带扩展参数的语音请求。</summary>
    private static SpeechRequest Request() => new()
    { Model = "voice", Text = "你好", Voice = "speaker", SampleRate = 24000, Parameters = new() { ["future"] = "kept" } };
    /// <summary>返回分段 JSON，覆盖 JSON 文本和网络包边界不同的情况。</summary>
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new FragmentedReadStream(body)) };
    /// <summary>完整读取音频流以触发终态检查。</summary>
    private static async Task<List<SpeechChunk>> CollectAsync(IAsyncEnumerable<SpeechChunk> source)
    {
        List<SpeechChunk> chunks = [];
        await foreach (var chunk in source) chunks.Add(chunk);
        return chunks;
    }
}
