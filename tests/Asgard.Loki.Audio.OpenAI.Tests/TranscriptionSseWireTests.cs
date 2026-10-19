using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>以独立完整 multipart/原生事件/终态快照验证真实 SDK 和直接供应商入口。</summary>
public sealed class TranscriptionSseWireTests
{
    /// <summary>交错分段引用、未知事件、原生用量/语言/概率、SSE 身份以及原始文本均保留。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task FullNativeStream_PreservesWireEventsAndCompletion(bool direct, bool length)
    {
        var wire = TranscriptionSseFixture.Wire(); var stream = TranscriptionSseFixture.Stream(wire);
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://audio.invalid/tenant/v1/audio/transcriptions", message.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-file-secret", message.Headers.Authorization.Parameter);
            Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
            var actual = await AudioMultipartWire.Read(message.Content!, token);
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("transcription-stream-request"), actual));
            return TranscriptionSseFixture.Response(stream, length ? Encoding.UTF8.GetByteCount(wire) : null);
        });
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        await using var client = AudioFileHttpFixture.Build(adapter, false);
        var request = AudioFileHttpFixture.Request(upload, "diarized"); List<AudioTranscriptionEvent> seen = [];
        var source = direct ? adapter.StreamAsync(request, TestContext.Current.CancellationToken) : client.Audio.Transcriptions.StreamAsync(request, TestContext.Current.CancellationToken);
        await foreach (var item in source)
        {
            if (item.IsCompleted) { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); }
            else { Assert.False(stream.Disposed); Assert.False(factory.Clients[0].Disposed); }
            seen.Add(item);
        }
        var expected = SpeechFixture.Golden("transcription-stream-events"); Assert.Equal(expected.GetArrayLength(), seen.Count);
        for (var index = 0; index < seen.Count; index++)
        {
            Assert.True(JsonElement.DeepEquals(expected[index], seen[index].Data)); Assert.Equal(expected[index].GetRawText(), seen[index].Data.GetRawText());
            Assert.Equal(new AudioResultMetadata("recording", "file-upstream-fixture", "file-target-one"), seen[index].Metadata);
            Assert.Equal(123, seen[index].RetryMilliseconds); Assert.Equal(index == seen.Count - 1, seen[index].IsCompleted);
        }
        Assert.Equal(new[] { "wire_first", "wire_first", "wire_second", "wire_second", "wire_second", "wire_done" }, seen.Select(item => item.EventId));
        Assert.Equal("seg_A", seen[3].Data.GetProperty("id").GetString()); Assert.Equal("wire_second", seen[3].EventId);
        Assert.Null(seen[1].EventName);
        Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("transcription-stream-terminal"), JsonSerializer.SerializeToElement(seen[^1])));
        Assert.False(upload.Disposed); Assert.True(upload.Eof); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>流式方法只绑定明确 stream=true；已给出的 true 保持原位，格式等默认参数不注入。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StreamControl_IsExplicitAndMinimal(bool explicitTrue)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var request = AudioFileHttpFixture.Request(upload, "minimal");
        if (explicitTrue) request = request with { Fields = [new("stream", "true")] };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("transcription-plain-stream-request"), await AudioMultipartWire.Read(message.Content!, token)));
            return TranscriptionSseFixture.Response(TranscriptionSseFixture.Stream(TranscriptionSseFixture.Event("""{"type":"transcript.text.done","text":""}""")));
        });
        var result = await TranscriptionSseFixture.Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options)
            .StreamAsync(request, TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(result).IsCompleted); Assert.Equal("", result[0].Data.GetProperty("text").GetString());
    }

    /// <summary>UTF-8 BOM、多行正文、不同换行和传输 id 重置按 SSE 原生规则保留。</summary>
    [Theory] [InlineData("\n")] [InlineData("\r\n")] [InlineData("\r")]
    public async Task FramingAndIdentity_AreIndependentOfNativeSegmentIds(string newline)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = "\uFEFFid: transport\nretry: 10\nevent: message\ndata: {\"type\":\"transcript.text.delta\",\ndata: \"delta\":\"你好\"}\n\nid:\n"
            + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        body = body.Replace("\n", newline, StringComparison.Ordinal);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(TranscriptionSseFixture.Response(TranscriptionSseFixture.Stream(body))));
        var items = await TranscriptionSseFixture.Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options)
            .StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.Equal(2, items.Count); Assert.Equal("message", items[0].EventName); Assert.Equal("transport", items[0].EventId);
        Assert.Equal("", items[1].EventId); Assert.Equal(10, items[1].RetryMilliseconds);
        Assert.Equal("{\"type\":\"transcript.text.delta\",\n\"delta\":\"你好\"}", items[0].Data.GetRawText());
    }

    /// <summary>等价整数和高精度有序时间保持原始数值文本，不以解析后的浮点值重编码事件。</summary>
    [Fact]
    public async Task ExactNumericValidation_PreservesValidNativeLexemes()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        const string delta = """{"type":"transcript.text.delta","delta":"native","logprobs":[{"bytes":[1.0,2.55e2,-0,0e999]}]}""";
        const string segment = """{"type":"transcript.text.segment","id":"precise","start":1.0000000000000000000001,"end":1.0000000000000000000002,"text":"native","speaker":"A"}""";
        var body = TranscriptionSseFixture.Event(delta) + TranscriptionSseFixture.Event(segment) + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(TranscriptionSseFixture.Response(TranscriptionSseFixture.Stream(body))));
        var items = await TranscriptionSseFixture.Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options)
            .StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.Equal(delta, items[0].Data.GetRawText()); Assert.Equal(segment, items[1].Data.GetRawText()); Assert.True(items[^1].IsCompleted);
    }

    /// <summary>原生最终文本、未知用量口径、可选 null 保持原文，不按增量/概率重算文本或费用。</summary>
    [Theory]
    [InlineData("""{"type":"transcript.text.done","text":"native final revised","languages":[],"logprobs":null,"usage":null}""")]
    [InlineData("""{"type":"transcript.text.done","text":"native final revised","usage":{"type":"duration","seconds":1.75,"future":false}}""")]
    public async Task NativeFinalSnapshots_AreNotReconstructed(string done)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta) + TranscriptionSseFixture.Event(done);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(TranscriptionSseFixture.Response(TranscriptionSseFixture.Stream(body))));
        var items = await TranscriptionSseFixture.Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options)
            .StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.Equal(done, items[^1].Data.GetRawText()); Assert.Equal("native final revised", items[^1].Data.GetProperty("text").GetString());
    }
}
