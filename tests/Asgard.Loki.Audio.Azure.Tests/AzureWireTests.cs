using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>独立样本通过公共 SDK 验证实际部署、完整字段及原始二进制/文本。</summary>
public sealed class AzureWireTests
{
    /// <summary>公共模型不进入 URL 或请求体；可选体模型与部署名明确分开。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task SpeechNativeWire(int index)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Target(message, "speech"); using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal("application/json", message.Content.Headers.ContentType!.MediaType);
            Assert.True(JsonElement.DeepEquals(AzureFixture.Root.GetProperty("speech_requests")[index], body.RootElement));
            var response = AzureFixture.Response(AzureFixture.Audio, index == 2 ? "application/octet-stream" : "audio/pcm");
            response.Headers.Add("apim-request-id", "original-request-id"); return response;
        });
        var factory = new AzureTestFactory(handler); var adapter = new AzureSpeechAdapter(factory, AzureFixture.Options(bodyModel: index == 1));
        await using (var client = new LokiClientBuilder().AddAudio(new AudioOptions { SpeechRoutes = [new() { Model = "public-speech", Targets = [adapter] }] }).Build())
        {
            var result = await client.Audio.Speech.GenerateNativeAsync(AzureFixture.Speech(index), TestContext.Current.CancellationToken);
            Assert.Equal(AzureFixture.Audio, result.Audio.ToArray()); Assert.True(result.Frames[^1].GetProperty("completed").GetBoolean());
            Assert.All(result.Frames, frame => { Assert.Equal("azure.audio.http", frame.GetProperty("kind").GetString());
                Assert.Equal("fixture-deployment.v1", frame.GetProperty("deployment").GetString()); Assert.Equal("public-speech", frame.GetProperty("public_model").GetString());
                Assert.Equal("original-request-id", frame.GetProperty("headers").GetProperty("apim-request-id")[0].GetString()); Assert.False(frame.TryGetProperty("usage", out _)); });
            Assert.Equal(1, factory.ClientDisposals);
        }
        Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>转录/翻译发送原始 multipart 和文件字节，返回完整 JSON/CRLF 字幕，不重写语言。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FileNativeWire(bool translate)
    {
        using var upload = new AzureReadStream(AzureFixture.Upload); var bytes = AzureFixture.Result(translate);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Target(message, translate ? "translations" : "transcriptions"); Assert.Null(message.Content!.Headers.ContentLength);
            var parts = await AzureMultipartWire.Read(message.Content, token);
            Assert.True(JsonElement.DeepEquals(AzureFixture.Root.GetProperty(translate ? "translation_parts" : "transcription_parts"), parts), parts.GetRawText());
            return AzureFixture.Response(bytes, translate ? "text/plain" : "application/json");
        });
        var factory = new AzureTestFactory(handler); var adapter = new AzureAudioFileAdapter(factory, AzureFixture.Options());
        await using (var client = new LokiClientBuilder().AddAudio(new AudioOptions { TranscriptionRoutes = [new() { Model = "public-file", Targets = [adapter] }], TranslationRoutes = [new() { Model = "public-file", Targets = [adapter] }] }).Build())
        {
            var result = translate ? await client.Audio.Translations.CreateAsync(AzureFixture.File(upload, true), TestContext.Current.CancellationToken)
                : await client.Audio.Transcriptions.CreateAsync(AzureFixture.File(upload), TestContext.Current.CancellationToken);
            Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal(new AudioResultMetadata("public-file", "fixture-deployment.v1", "azure-native"), result.Metadata);
            Assert.False(upload.Disposed); Assert.True(upload.Eof); Assert.Equal(1, factory.ClientDisposals);
        }
        Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>便捷语音只映射公共字段，扩展在 HTTP 工厂执行前冻结。</summary>
    [Fact]
    public async Task ConvenienceSnapshot()
    {
        var root = JsonNode.Parse(AzureFixture.Root.GetProperty("speech_requests")[0].GetRawText())!.AsObject();
        var text = root["input"]!.GetValue<string>(); var voice = root["voice"]!.GetValue<string>();
        foreach (var name in new[] { "input", "voice", "response_format" }) root.Remove(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(AzureFixture.Root.GetProperty("speech_requests")[0], body.RootElement)); return AzureFixture.Response(AzureFixture.Audio, "audio/pcm");
        });
        var factory = new AzureTestFactory(handler) { OnCreate = root.Clear }; var adapter = new AzureSpeechAdapter(factory, AzureFixture.Options());
        var stream = adapter.StreamAsync(new() { Model = "public-speech", Text = text, Voice = voice, Format = "pcm", Parameters = root }, TestContext.Current.CancellationToken);
        Assert.True((await AzureFixture.Collect(stream))[^1].IsCompleted); Assert.Equal(1, factory.ClientDisposals);
    }
    /// <summary>HTTP/业务失败保留完整原生字节及实际状态，二进制错误不经有损解码。</summary>
    [Theory] [InlineData(false, 400)] [InlineData(false, 200)] [InlineData(true, 400)] [InlineData(true, 200)]
    public async Task OriginalErrors(bool file, int code)
    {
        var bytes = Encoding.UTF8.GetBytes(AzureFixture.Root.GetProperty("error_response").GetRawText());
        using var upload = new AzureReadStream(AzureFixture.Upload);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Response(bytes, "application/json", (HttpStatusCode)code)));
        var factory = new AzureTestFactory(handler);
        var error = file ? await Assert.ThrowsAsync<AzureAudioException>(() => new AzureAudioFileAdapter(factory, AzureFixture.Options()).TranscribeAsync(AzureFixture.File(upload), TestContext.Current.CancellationToken))
            : await Assert.ThrowsAsync<AzureAudioException>(() => AzureFixture.Collect(new AzureSpeechAdapter(factory, AzureFixture.Options()).StreamNativeAsync(AzureFixture.Speech(), TestContext.Current.CancellationToken)));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(code, error.StatusCode); Assert.Equal(1, handler.Calls); Assert.False(upload.Disposed); Assert.Equal(1, factory.ClientDisposals);
    }
    /// <summary>文件参数在工厂前冻结，可选模型 part 使用固定原值，原始音频从当前位置发送。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FileSnapshotAndOptionalModel(bool bodyModel)
    {
        using var upload = new AzureReadStream([9, 8, .. AzureFixture.Upload]); await upload.ReadExactlyAsync(new byte[2], TestContext.Current.CancellationToken);
        var fields = AzureFixture.File(upload).Fields.ToList();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var parts = await AzureMultipartWire.Read(message.Content!, token); var offset = bodyModel ? 1 : 0;
            if (bodyModel)
            {
                Assert.Equal("fixture-body-model", parts[0].GetProperty("text").GetString());
                Assert.Equal("Content-Disposition: form-data; name=model", parts[0].GetProperty("headers")[1].GetString());
            }
            var expected = AzureFixture.Root.GetProperty("transcription_parts"); Assert.Equal(expected.GetArrayLength() + offset, parts.GetArrayLength());
            for (var index = 0; index < expected.GetArrayLength(); index++) Assert.True(JsonElement.DeepEquals(expected[index], parts[index + offset]));
            return AzureFixture.Response(AzureFixture.Result(false));
        });
        var factory = new AzureTestFactory(handler) { OnCreate = fields.Clear };
        await new AzureAudioFileAdapter(factory, AzureFixture.Options(bodyModel)).TranscribeAsync(AzureFixture.File(upload) with { Fields = fields }, TestContext.Current.CancellationToken);
        Assert.False(upload.Disposed); Assert.Equal(1, factory.ClientDisposals);
    }
    private static void Target(HttpRequestMessage message, string operation)
    {
        Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://fixture-resource.example.invalid/openai/deployments/fixture-deployment.v1/audio/" + operation + "?api-version=2025-04-01-preview", message.RequestUri!.AbsoluteUri);
        Assert.Equal("fixture-independent-audio-key", Assert.Single(message.Headers.GetValues("api-key")));
        Assert.Null(message.Headers.Authorization); Assert.False(message.Headers.Contains("OpenAI-Organization"));
    }
}
