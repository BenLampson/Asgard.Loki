using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.MiniMax.Tests;

/// <summary>完整原生调用、公共桥接与资源终态的离线测试。</summary>
public sealed class MiniMaxSpeechTests
{
    /// <summary>实际模型与公共路由独立，原始参数及不同输出载体完整保留。</summary>
    [Theory]
    [InlineData(0, "hex")]
    [InlineData(1, "url")]
    [InlineData(2, "hex")]
    public async Task PublicNativeRoundTrip(int index, string carrier)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://api.minimax.cn/v1/t2a_v2", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-native-access", message.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Root.GetProperty("requests")[index], body.RootElement));
            return SpeechFixture.Response(carrier);
        });
        var factory = new SpeechHttpFactory(handler); var adapter = new MiniMaxSpeechAdapter(factory, SpeechFixture.Options());
        await using (var client = new LokiClientBuilder().AddAudio(new AudioOptions { SpeechRoutes = [new SpeechRoute { Model = "sdk-route", Targets = [adapter] }] }).Build())
        {
            var result = await client.Audio.Speech.GenerateNativeAsync(SpeechFixture.Request(index), TestContext.Current.CancellationToken);
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Root.GetProperty("responses").GetProperty(carrier), Assert.Single(result.Frames)));
            Assert.Equal(carrier == "url" ? [] : new byte[] { 0, 1, 171, 255, 2 }, result.Audio.ToArray());
            if (carrier == "url") Assert.Equal(SpeechFixture.Root.GetProperty("responses").GetProperty("url").GetProperty("data").GetProperty("audio").GetString(),
                result.Frames[0].GetProperty("data").GetProperty("audio").GetString());
            Assert.Equal(1, factory.ClientDisposals);
        }
        Assert.Equal(0, factory.Disposals); Assert.Equal(1, handler.Calls); // 没有隐式音频或字幕下载。
    }
    /// <summary>原生请求与公共桥接在工厂执行前冻结，扩展不被丢弃。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotAndTypedBridge(bool typed)
    {
        var native = SpeechFixture.Request(); var root = JsonNode.Parse(SpeechFixture.Root.GetProperty("requests")[0].GetRawText())!.AsObject();
        root.Remove("model"); root.Remove("text"); var voice = root["voice_setting"]!.AsObject(); voice.Remove("voice_id");
        var audio = root["audio_setting"]!.DeepClone().AsObject(); audio.Remove("format"); audio.Remove("sample_rate"); root.Remove("audio_setting");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var sent = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            var actual = sent.RootElement;
            if (!typed) Assert.True(JsonElement.DeepEquals(SpeechFixture.Root.GetProperty("requests")[0], actual));
            else
            {
                Assert.Equal("公共文本 😀", actual.GetProperty("text").GetString()); Assert.Equal("native-voice", actual.GetProperty("voice_setting").GetProperty("voice_id").GetString());
                Assert.Equal(22050, actual.GetProperty("audio_setting").GetProperty("sample_rate").GetInt32());
                Assert.Equal("pcm", actual.GetProperty("audio_setting").GetProperty("format").GetString());
                Assert.Equal(9007199254740993L, actual.GetProperty("Future").GetProperty("Big").GetInt64());
                Assert.False(actual.GetProperty("voice_setting").GetProperty("text_normalization").GetBoolean());
            }
            return SpeechFixture.Response();
        });
        var factory = new SpeechHttpFactory(handler) { OnCreate = () => { root.Clear(); audio.Clear(); native.Parameters.Clear(); } };
        var adapter = new MiniMaxSpeechAdapter(factory, SpeechFixture.Options());
        var stream = typed ? adapter.StreamAsync(new SpeechRequest { Model = "sdk-route", Text = "公共文本 😀", Voice = "native-voice", Format = "pcm", SampleRate = 22050, Parameters = root, AudioParameters = audio }, TestContext.Current.CancellationToken)
            : adapter.StreamNativeAsync(native, TestContext.Current.CancellationToken);
        var chunks = await SpeechFixture.Collect(stream); Assert.True(Assert.Single(chunks).IsCompleted);
        Assert.Equal(1, factory.ClientDisposals); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>原生错误保留完整 JSON、原始正文及大整数，不转换成 LLM 用量。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorPreservation(bool httpError)
    {
        var text = httpError ? "raw native http failure\n保留" : SpeechFixture.Root.GetProperty("responses").GetProperty("error").GetRawText();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.JsonResponse(text, httpError ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)));
        var factory = new SpeechHttpFactory(handler);
        var error = await Assert.ThrowsAsync<MiniMaxSpeechException>(() => SpeechFixture.Collect(new MiniMaxSpeechAdapter(factory, SpeechFixture.Options())
            .StreamNativeAsync(SpeechFixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(httpError ? null : (long?)1004, error.NativeCode); Assert.Equal(Encoding.UTF8.GetBytes(text), error.RawResponse.ToArray());
        Assert.Equal(httpError ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, error.StatusCode);
        Assert.Equal(httpError, error.NativeData is null); Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals);
    }
    /// <summary>HTTP 读完后仍须通过外层释放和最后取消检查。</summary>
    [Theory]
    [InlineData("content-dispose")]
    [InlineData("cancel-dispose")]
    [InlineData("cancel-handler")]
    [InlineData("client-dispose")]
    [InlineData("cancel-client-dispose")]
    public async Task LateFailures(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new StubHttpHandler((_, _) =>
        {
            if (kind == "cancel-handler") cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new SpeechContent(Encoding.UTF8.GetBytes(SpeechFixture.Root.GetProperty("responses").GetProperty("hex").GetRawText()), () =>
                { if (kind == "content-dispose") throw new IOException("content dispose"); if (kind == "cancel-dispose") cancellation.Cancel(); }) });
        });
        var factory = new SpeechHttpFactory(handler) { OnClientDispose = () =>
            { if (kind == "client-dispose") throw new IOException("client dispose"); if (kind == "cancel-client-dispose") cancellation.Cancel(); } };
        var chunks = new List<SpeechChunk>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var chunk in new MiniMaxSpeechAdapter(factory, SpeechFixture.Options()).StreamNativeAsync(SpeechFixture.Request(), cancellation.Token)) chunks.Add(chunk);
        });
        Assert.Empty(chunks); Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.Equal(0, factory.Disposals);
    }

    /// <summary>业务码保持 Int64 原值，原生失败 JSON 不丢失精确数字。</summary>
    [Fact]
    public async Task LargeNativeErrorCode()
    {
        var root = JsonNode.Parse(SpeechFixture.Root.GetProperty("responses").GetProperty("error").GetRawText())!;
        root["base_resp"]!["status_code"] = 9007199254740993L;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.JsonResponse(root.ToJsonString())));
        var error = await Assert.ThrowsAsync<MiniMaxSpeechException>(() => SpeechFixture.Collect(new MiniMaxSpeechAdapter(new StubHttpClientFactory(handler), SpeechFixture.Options())
            .StreamNativeAsync(SpeechFixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(9007199254740993L, error.NativeCode); Assert.Equal(9007199254740993L, error.NativeData!.Value.GetProperty("base_resp").GetProperty("status_code").GetInt64());
    }
}
