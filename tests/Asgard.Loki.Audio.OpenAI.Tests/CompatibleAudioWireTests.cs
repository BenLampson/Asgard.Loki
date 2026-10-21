using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>显式兼容配置通过已有公共 SDK 和传输，不创建供应商品牌包或跨能力状态。</summary>
public sealed class CompatibleAudioWireTests
{
    /// <summary>原生/便捷请求保留实际模型、原始音色与未知字段，省略参数不被补造。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)]
    public async Task CompleteSpeechProfile(int index, bool convenience)
    {
        var bytes = CompatibleAudioFixture.Audio; var source = new SpeechReadStream(bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://api.siliconflow.cn/v1/audio/speech", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-compatible-speech", message.Headers.Authorization!.ToString());
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(CompatibleAudioFixture.Root.GetProperty("speech_requests")[index], body.RootElement));
            var response = SpeechFixture.Response(source, index == 0 ? "audio/pcm" : "audio/mpeg", bytes.Length);
            response.Headers.Add("x-siliconcloud-trace-id", "original-profile-trace"); return response;
        });
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiSpeechAdapter(factory, CompatibleAudioFixture.SpeechOptions);
        await using (var client = SpeechFixture.Build(adapter))
        {
            IReadOnlyList<JsonElement> frames; ReadOnlyMemory<byte> audio;
            if (convenience)
            {
                var golden = CompatibleAudioFixture.Root.GetProperty("speech_requests")[index];
                var parameters = JsonNode.Parse(golden.GetRawText())!.AsObject(); foreach (var key in new[] { "model", "input", "voice", "response_format" }) parameters.Remove(key);
                var result = await client.Audio.Speech.GenerateAsync(new() { Model = "voice", Text = golden.GetProperty("input").GetString()!,
                    Voice = golden.GetProperty("voice").GetString()!, Format = "pcm", Parameters = parameters }, TestContext.Current.CancellationToken);
                audio = result.Audio; frames = result.Frames;
            }
            else
            {
                var result = await client.Audio.Speech.GenerateNativeAsync(CompatibleAudioFixture.Speech(index), TestContext.Current.CancellationToken);
                audio = result.Audio; frames = result.Frames;
            }
            Assert.Equal(bytes, audio.ToArray()); Assert.Equal(4, frames.Count);
            Assert.All(frames, frame =>
            {
                Assert.Equal("voice", frame.GetProperty("public_model").GetString()); Assert.Equal("fixture-native-tts", frame.GetProperty("target_model").GetString());
                Assert.Equal("original-profile-trace", frame.GetProperty("headers").GetProperty("x-siliconcloud-trace-id")[0].GetString());
                Assert.False(frame.TryGetProperty("usage", out _));
            });
            Assert.True(frames[^1].GetProperty("completed").GetBoolean()); Assert.True(source.Disposed); Assert.True(factory.Clients[0].Disposed);
        }
        Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完整 multipart 每个 part 与独立样本匹配，公共路由和实际模型/凭据分别绑定。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompleteTranscriptionProfile(bool extended)
    {
        using var upload = new SpeechReadStream(CompatibleAudioFixture.Upload);
        var bytes = Encoding.UTF8.GetBytes(CompatibleAudioFixture.Root.GetProperty("transcription_response").GetRawText());
        var responseStream = new SpeechReadStream(bytes);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://api.siliconflow.cn/v1/audio/transcriptions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-compatible-file", message.Headers.Authorization!.ToString());
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            var wire = await AudioMultipartWire.Read(message.Content!, token);
            Assert.True(JsonElement.DeepEquals(CompatibleAudioFixture.Root.GetProperty("multipart_requests")[extended ? 1 : 0], wire), wire.GetRawText());
            var response = SpeechFixture.Response(responseStream, "application/json", bytes.Length);
            response.Content.Headers.ContentType!.CharSet = "utf-8"; return response;
        });
        var factory = new SpeechTestFactory(handler);
        await using (var client = AudioFileHttpFixture.Build(new(factory, CompatibleAudioFixture.FileOptions), false))
        {
            var result = await client.Audio.Transcriptions.CreateAsync(CompatibleAudioFixture.File(upload, extended), TestContext.Current.CancellationToken);
            Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal("application/json; charset=utf-8", result.ContentType);
            Assert.Equal(new AudioResultMetadata("recording", "fixture-native-asr", "siliconflow-explicit-asr"), result.Metadata);
            Assert.True(upload.Eof); Assert.False(upload.Disposed); Assert.True(responseStream.Disposed); Assert.True(factory.Clients[0].Disposed);
        }
        Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>供应商原始 HTTP/JSON 失败正文与状态保持原样，不重试或伪造原生成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeErrorsRemainOriginal(bool file)
    {
        var text = CompatibleAudioFixture.Root.GetProperty("error_response").GetRawText(); var bytes = Encoding.UTF8.GetBytes(text);
        var source = new SpeechReadStream(bytes); using var upload = new SpeechReadStream(CompatibleAudioFixture.Upload);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(source, "application/json", bytes.Length, HttpStatusCode.BadRequest)));
        var factory = new SpeechTestFactory(handler);
        if (file)
        {
            var error = await Assert.ThrowsAsync<AudioFileUpstreamException>(() => new OpenAiAudioFileAdapter(factory, CompatibleAudioFixture.FileOptions)
                .TranscribeAsync(CompatibleAudioFixture.File(upload), TestContext.Current.CancellationToken));
            Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(400, error.Code); Assert.False(upload.Disposed);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SpeechUpstreamException>(() => SpeechFixture.Collect(new OpenAiSpeechAdapter(factory, CompatibleAudioFixture.SpeechOptions)
                .StreamNativeAsync(CompatibleAudioFixture.Speech(), TestContext.Current.CancellationToken)));
            Assert.Equal(text, error.ResponseBody); Assert.Equal(400, error.Code);
        }
        Assert.True(factory.Clients[0].Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
}
