using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>以独立完整请求样本检查原生 JSON 与便捷映射，不靠生产序列化逻辑生成期望。</summary>
public sealed class SpeechWireTests
{
    /// <summary>原生自定义声音及便捷声音都保留未知/null/false 字段，二进制结果不经过文本转换。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompleteWireSnapshot_PreservesFieldsAndBinary(bool native)
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes);
        var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://speech.invalid/tenant/v1/audio/speech", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-secret", message.Headers.Authorization.Parameter);
            Assert.Equal("org-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Organization")));
            Assert.Equal("project-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Project")));
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            using var sent = JsonDocument.Parse(await message.Content.ReadAsStringAsync(token));
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden(native ? "native" : "convenience"), sent.RootElement));
            return SpeechFixture.Response(stream);
        });
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        await using (var client = SpeechFixture.Build(adapter))
        {
            IReadOnlyList<JsonElement> frames; ReadOnlyMemory<byte> audio;
            if (native)
            {
                var result = await client.Audio.Speech.GenerateNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken);
                frames = result.Frames; audio = result.Audio;
            }
            else
            {
                var result = await client.Audio.Speech.GenerateAsync(Convenience(), TestContext.Current.CancellationToken);
                frames = result.Frames; audio = result.Audio; Assert.Equal("pcm", result.Format);
            }
            Assert.Equal(SpeechFixture.Bytes, audio.ToArray()); Assert.Equal(4, frames.Count);
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("terminal"), frames[^1]));
            Assert.Equal(new long[] { 3, 6, 8, 8 }, frames.Select(f => f.GetProperty("bytes_received").GetInt64()));
            Assert.Equal(new[] { false, false, false, true }, frames.Select(f => f.GetProperty("completed").GetBoolean()));
            Assert.All(frames, f =>
            {
                Assert.Equal("http_audio", f.GetProperty("transport").GetString()); Assert.Equal("voice", f.GetProperty("public_model").GetString());
                Assert.Equal("tts-upstream-fixture", f.GetProperty("target_model").GetString()); Assert.Equal(200, f.GetProperty("status_code").GetInt32());
                Assert.Equal("req_fixture", f.GetProperty("headers").GetProperty("X-Request-ID")[0].GetString());
                Assert.Equal(new[] { "one", "two" }, f.GetProperty("headers").GetProperty("x-future").EnumerateArray().Select(v => v.GetString()));
                Assert.False(f.TryGetProperty("usage", out _)); Assert.False(f.TryGetProperty("type", out _));
                Assert.DoesNotContain("fixture-secret", f.GetRawText());
            });
        }
        Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.False(factory.Disposed); Assert.Equal("Asgard.Loki.Audio.OpenAI", Assert.Single(factory.Names)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>已知格式与通用二进制类型原样交付；无长度的 EOF 也必须正常结束。</summary>
    [Theory]
    [InlineData("mp3", "audio/mpeg")] [InlineData("opus", "audio/opus")] [InlineData("aac", "audio/aac")]
    [InlineData("flac", "audio/flac")] [InlineData("wav", "audio/wav")] [InlineData("pcm", "audio/pcm")]
    [InlineData("future", "application/octet-stream")] [InlineData("future", "audio/future")]
    [InlineData("opus", "audio/ogg")]
    public async Task Formats_AreOpaqueBytesWithNoCodecOrModelGuessing(string format, string media)
    {
        var request = SpeechFixture.Native(); request.Parameters["response_format"] = JsonSerializer.SerializeToElement(format);
        var stream = new SpeechReadStream(SpeechFixture.Bytes);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream, media, null)));
        await using var client = SpeechFixture.Build(new(new SpeechTestFactory(handler), SpeechFixture.Options));
        var result = await client.Audio.Speech.GenerateNativeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(SpeechFixture.Bytes, result.Audio.ToArray()); Assert.True(result.Frames[^1].GetProperty("completed").GetBoolean());
    }

    /// <summary>原生省略/null 不注入默认字段，空 input 与非目录声音交给上游校验。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeOmissionOrNull_RemainsExactlyAsRequested(bool explicitNull)
    {
        var request = new NativeSpeechRequest { Model = "voice", Parameters = new()
        { ["input"] = JsonSerializer.SerializeToElement(""), ["voice"] = JsonSerializer.SerializeToElement("future_voice") } };
        if (explicitNull) foreach (var key in new[] { "response_format", "instructions", "speed", "stream_format" }) request.Parameters[key] = JsonSerializer.SerializeToElement<object?>(null);
        var handler = new StubHttpHandler(async (message, token) =>
        {
            using var sent = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token)); var body = sent.RootElement;
            Assert.Equal("https://api.openai.com/v1/audio/speech", message.RequestUri!.AbsoluteUri);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            Assert.Equal("future_model", body.GetProperty("model").GetString());
            Assert.Equal(explicitNull ? 7 : 3, body.EnumerateObject().Count()); Assert.Equal("", body.GetProperty("input").GetString());
            foreach (var key in new[] { "response_format", "instructions", "speed", "stream_format" })
            { if (explicitNull) Assert.Equal(JsonValueKind.Null, body.GetProperty(key).ValueKind); else Assert.False(body.TryGetProperty(key, out _)); }
            return SpeechFixture.Response(new(SpeechFixture.Bytes), explicitNull ? "audio/pcm" : "audio/mpeg");
        });
        var options = new OpenAiSpeechOptions { Model = "future_model", ApiKey = "fixture" };
        var chunks = await SpeechFixture.Collect(new OpenAiSpeechAdapter(new SpeechTestFactory(handler), options).StreamNativeAsync(request, TestContext.Current.CancellationToken));
        Assert.True(chunks[^1].IsCompleted);
    }

    /// <summary>适配器入口同步快照；请求源文档可在开始枚举前释放，嵌套便捷对象也可修改。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DirectAdapter_SnapshotsBeforeEnumeration(bool native)
    {
        var request = SpeechFixture.Native(); var common = Convenience();
        using var source = JsonDocument.Parse("""{"id":"voice_fixture","future":{"enabled":false,"value":null}}""");
        request.Parameters["voice"] = source.RootElement;
        var handler = new StubHttpHandler(async (message, token) =>
        {
            using var actual = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden(native ? "native" : "convenience"), actual.RootElement));
            return SpeechFixture.Response(new(SpeechFixture.Bytes));
        });
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        var pending = native ? adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken) : adapter.StreamAsync(common, TestContext.Current.CancellationToken);
        request.Parameters.Clear(); source.Dispose(); common.Parameters["future"]![2]!["nested"]![1] = "changed";
        Assert.True((await SpeechFixture.Collect(pending))[^1].IsCompleted);
    }

    /// <summary>便捷输入独立构造，公共字段由该接口负责，其余字段取自独立样本。</summary>
    internal static SpeechRequest Convenience()
    {
        var body = JsonNode.Parse(SpeechFixture.Golden("convenience").GetRawText())!.AsObject();
        foreach (var field in new[] { "model", "input", "voice", "response_format" }) body.Remove(field);
        return new() { Model = "voice", Text = "你好\nvoice", Voice = "voice_future", Format = "pcm", Parameters = body };
    }
}
