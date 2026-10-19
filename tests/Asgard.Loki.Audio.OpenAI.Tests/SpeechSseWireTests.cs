using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>原生 SSE 正向样本及事件身份、未知字段、音频关联和聚合快照。</summary>
public sealed class SpeechSseWireTests
{
    /// <summary>完整请求和完整事件序列保真，未知事件不转音频，done 保持原始用量。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompleteSseSnapshot_PreservesAudioEventsAndEnvelope(bool aggregate)
    {
        var wire = SpeechSseFixture.Wire(); var stream = SpeechSseFixture.Stream(wire);
        var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://speech.invalid/tenant/v1/audio/speech", message.RequestUri!.AbsoluteUri);
            Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
            using var sent = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("sse-request"), sent.RootElement));
            return SpeechSseFixture.Response(stream);
        });
        var factory = new SpeechTestFactory(handler); await using var client = SpeechFixture.Build(new(factory, SpeechFixture.Options));
        IReadOnlyList<JsonElement> frames; byte[] bytes;
        if (aggregate)
        {
            var result = await client.Audio.Speech.GenerateNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken);
            frames = result.Frames; bytes = result.Audio.ToArray();
        }
        else
        {
            var chunks = new List<SpeechChunk>();
            await foreach (var chunk in client.Audio.Speech.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken))
            {
                if (chunk.IsCompleted) { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); }
                else { Assert.False(stream.Disposed); Assert.False(factory.Clients[0].Disposed); }
                chunks.Add(chunk);
            }
            Assert.Equal(new[] { false, false, false, true }, chunks.Select(c => c.IsCompleted));
            Assert.Empty(chunks[1].Audio.ToArray()); Assert.Empty(chunks[^1].Audio.ToArray());
            frames = chunks.Select(c => c.Data).ToArray(); bytes = chunks.SelectMany(c => c.Audio.ToArray()).ToArray();
        }
        Assert.Equal(SpeechFixture.Bytes, bytes); Assert.Equal(4, frames.Count);
        var golden = SpeechFixture.Golden("sse-events");
        for (var index = 0; index < frames.Count; index++)
        {
            Assert.True(JsonElement.DeepEquals(golden[index], frames[index].GetProperty("data")));
            Assert.True(JsonElement.DeepEquals(golden[index], JsonSerializer.Deserialize<JsonElement>(frames[index].GetProperty("raw_data").GetString()!)));
            Assert.Equal(123, frames[index].GetProperty("retry_ms").GetInt64());
        }
        Assert.Equal(new[] { "frame_one", "frame_one", "frame_two", "frame_done" }, frames.Select(f => f.GetProperty("event_id").GetString()));
        Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("sse-terminal"), frames[^1]));
        Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>便捷入口通过明确原生参数选择 SSE，聚合的格式仍取自明确请求。</summary>
    [Fact]
    public async Task ConvenienceRequest_CanExplicitlySelectSse()
    {
        var request = SpeechWireTests.Convenience(); request.Parameters["stream_format"] = "sse";
        var handler = new StubHttpHandler(async (message, token) =>
        {
            using var sent = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("sse", sent.RootElement.GetProperty("stream_format").GetString());
            Assert.Equal("voice_future", sent.RootElement.GetProperty("voice").GetString());
            return SpeechSseFixture.Response(SpeechSseFixture.Stream(SpeechSseFixture.Wire()));
        });
        await using var client = SpeechFixture.Build(new(new SpeechTestFactory(handler), SpeechFixture.Options));
        var result = await client.Audio.Speech.GenerateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("pcm", result.Format); Assert.Equal(SpeechFixture.Bytes, result.Audio.ToArray());
    }

    /// <summary>UTF-8 BOM、多行 data、不同换行和默认 message 事件均按原始 SSE 规则保留。</summary>
    [Theory] [InlineData("\n")] [InlineData("\r\n")] [InlineData("\r")]
    public async Task SseFraming_PreservesDataAndResetIdentity(string newline)
    {
        var wire = "\uFEFF: start\nid: first\nretry: 2\nevent: speech.audio.delta\ndata: {\"type\":\"speech.audio.delta\",\ndata: \"audio\":\"AP+A\"}\n\n"
            + "id:\nretry: invalid\nevent: message\n" + SpeechSseFixture.Event(SpeechSseFixture.Done);
        wire = wire.Replace("\n", newline, StringComparison.Ordinal);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechSseFixture.Response(SpeechSseFixture.Stream(wire), Encoding.UTF8.GetByteCount(wire))));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        var chunks = await SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 0, 255, 128 }, chunks[0].Audio.ToArray()); Assert.Equal("", chunks[1].Data.GetProperty("event_id").GetString());
        Assert.Equal(2, chunks[1].Data.GetProperty("retry_ms").GetInt64()); Assert.Equal("message", chunks[1].Data.GetProperty("event_name").GetString());
        Assert.Equal("{\"type\":\"speech.audio.delta\",\n\"audio\":\"AP+A\"}", chunks[0].Data.GetProperty("raw_data").GetString());
    }

    /// <summary>原生整数字段不限于 Int64，并保留未知用量，SDK 不推导或改写总数。</summary>
    [Fact]
    public async Task NativeUsage_PreservesLargeIntegersAndDoesNotRecalculate()
    {
        const string done = "{\"type\":\"speech.audio.done\",\"usage\":{\"input_tokens\":123456789012345678901234567890,\"output_tokens\":0,\"total_tokens\":7,\"future\":false}}";
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechSseFixture.Response(SpeechSseFixture.Stream(SpeechSseFixture.Event(done)))));
        var chunks = await SpeechFixture.Collect(new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options).StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken));
        var only = Assert.Single(chunks); Assert.True(only.IsCompleted); Assert.True(only.Audio.IsEmpty);
        Assert.Equal(done, only.Data.GetProperty("raw_data").GetString()); Assert.Equal("123456789012345678901234567890", only.Data.GetProperty("data").GetProperty("usage").GetProperty("input_tokens").GetRawText());
    }
}
