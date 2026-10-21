using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>公共 SDK 的原生二进制/JSON 输入与完整响应保真。</summary>
public sealed class AudioWireTests
{
    /// <summary>完整原生请求、固定目标及响应字节均与独立资源匹配。</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task PublicNativeRoundTrip(int index, bool translate)
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes) { ChunkSize = 2 };
        var format = index == 0 ? CloudflareAudioInputFormat.RawBinary : CloudflareAudioInputFormat.JsonBase64;
        var responseName = index == 0 ? "raw" : "json";
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var model = format == CloudflareAudioInputFormat.RawBinary ? "whisper" : "whisper-large-v3-turbo";
            Assert.Equal("https://api.cloudflare.com/client/v4/accounts/fixture-account/ai/run/@cf/openai/" + model, message.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer fixture-native-access", message.Headers.Authorization!.ToString());
            Assert.Null(message.Content!.Headers.ContentLength); Assert.Equal(0, upload.Consumed);
            using var body = new MemoryStream(); await message.Content.CopyToAsync(body, token);
            if (index == 0) { Assert.Equal("audio/wav", message.Content.Headers.ContentType!.ToString()); Assert.Equal(AudioFixture.Bytes, body.ToArray()); }
            else
            {
                Assert.Equal("application/json", message.Content.Headers.ContentType!.ToString()); using var json = JsonDocument.Parse(body.ToArray());
                Assert.True(JsonElement.DeepEquals(AudioFixture.Root.GetProperty("requests")[index].GetProperty("body"), json.RootElement));
            }
            return AudioFixture.Response(responseName);
        });
        var factory = new AudioTestFactory(handler); var adapter = new CloudflareAudioFileAdapter(factory, AudioFixture.Options(format));
        var options = new AudioOptions { TranscriptionRoutes = [new() { Model = "public-audio", Targets = [adapter] }], TranslationRoutes = [new() { Model = "public-audio", Targets = [adapter] }] };
        await using (var client = new LokiClientBuilder().AddAudio(options).Build())
        {
            var request = AudioFixture.Request(upload, index);
            var result = translate ? await client.Audio.Translations.CreateAsync(request, TestContext.Current.CancellationToken)
                : await client.Audio.Transcriptions.CreateAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(Encoding.UTF8.GetBytes(AudioFixture.Root.GetProperty("responses").GetProperty(responseName).GetRawText()), result.Body.ToArray());
            Assert.Equal("public-audio", result.Metadata.PublicModel); Assert.Equal(AudioFixture.Options(format).Model, result.Metadata.UpstreamModel);
            Assert.Equal(adapter.Id, result.Metadata.ProviderId); Assert.Equal("application/json; charset=utf-8", result.ContentType);
            Assert.False(upload.Disposed); Assert.Equal(1, factory.ClientDisposals);
        }
        Assert.Equal(0, factory.Disposals); Assert.Equal(1, handler.Calls);
    }
    /// <summary>从当前位置发送且正确处理 Base64 尾部，不按读块添加独立填充。</summary>
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 2)] [InlineData(3, 1)] [InlineData(4, 2)] [InlineData(5, 5)] [InlineData(17, 2)]
    public async Task JsonBase64PreservesRemainingBytes(int length, int fragment)
    {
        var bytes = Enumerable.Range(0, length).Select(x => (byte)(x * 17)).ToArray();
        using var upload = new AudioUploadProbe([9, 8, .. bytes]) { ChunkSize = fragment };
        await upload.ReadExactlyAsync(new byte[2], TestContext.Current.CancellationToken);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = new MemoryStream(); await message.Content!.CopyToAsync(body, token); using var json = JsonDocument.Parse(body.ToArray());
            Assert.Equal(Convert.ToBase64String(bytes), json.RootElement.GetProperty("audio").GetString());
            Assert.Equal("transcribe", json.RootElement.GetProperty("task").GetString()); return AudioFixture.Response("empty");
        });
        var result = await new CloudflareAudioFileAdapter(new StubHttpClientFactory(handler), AudioFixture.Options(CloudflareAudioInputFormat.JsonBase64))
            .TranscribeAsync(AudioFixture.Request(upload) with { Fields = [] }, TestContext.Current.CancellationToken);
        Assert.False(upload.Disposed); using var response = JsonDocument.Parse(result.Body); Assert.Equal("", response.RootElement.GetProperty("result").GetProperty("text").GetString());
    }
    /// <summary>字段在 HTTP 工厂执行前冻结，原生 JSON 类型与输入流身份保持原样。</summary>
    [Fact]
    public async Task MetadataSnapshotBeforeFactory()
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes);
        var fields = new List<AudioMultipartField> { new("language", "\"zh\""), new("vad_filter", "false"), new("Future", "{\"Exact\":9007199254740993,\"Null\":null}") };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = new MemoryStream(); await message.Content!.CopyToAsync(body, token); using var json = JsonDocument.Parse(body.ToArray());
            Assert.Equal("zh", json.RootElement.GetProperty("language").GetString()); Assert.False(json.RootElement.GetProperty("vad_filter").GetBoolean());
            Assert.Equal(9007199254740993L, json.RootElement.GetProperty("Future").GetProperty("Exact").GetInt64()); return AudioFixture.Response("json");
        });
        var factory = new AudioTestFactory(handler) { OnCreate = fields.Clear };
        await new CloudflareAudioFileAdapter(factory, AudioFixture.Options(CloudflareAudioInputFormat.JsonBase64))
            .TranscribeAsync(AudioFixture.Request(upload) with { Fields = fields }, TestContext.Current.CancellationToken);
        Assert.False(upload.Disposed); Assert.Equal(1, factory.ClientDisposals); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>两种编码均拒绝隐式重放原始输入，回放失败不关闭借用流。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CannotReplay(bool json)
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = new MemoryStream(); await message.Content!.CopyToAsync(body, token);
            var consumed = upload.Consumed;
            await Assert.ThrowsAsync<InvalidOperationException>(() => message.Content.CopyToAsync(body, token));
            Assert.Equal(consumed, upload.Consumed); return AudioFixture.Response();
        });
        await new CloudflareAudioFileAdapter(new StubHttpClientFactory(handler), AudioFixture.Options(json ? CloudflareAudioInputFormat.JsonBase64 : CloudflareAudioInputFormat.RawBinary))
            .TranscribeAsync(AudioFixture.Request(upload), TestContext.Current.CancellationToken);
        Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls);
    }
}
