using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>拒绝原生事件字段歧义、音频损坏、错误完成及错误传输类型。</summary>
public sealed class SpeechSseValidationTests
{
    /// <summary>已知字段的错误类型和重复成员不能被未知字段保留策略掩盖。</summary>
    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("{}")] [InlineData("{\"type\":null}")]
    [InlineData("{\"type\":1}")] [InlineData("{\"type\":\" \"}")]
    [InlineData("{\"type\":\"speech.audio.delta\"}")] [InlineData("{\"type\":\"speech.audio.delta\",\"audio\":null}")]
    [InlineData("{\"type\":\"speech.audio.delta\",\"audio\":1}")]
    [InlineData("{\"type\":\"speech.audio.delta\",\"audio\":\"?\"}")]
    [InlineData("""{"type":"speech.audio.delta","audio":"AP+A "}""")]
    [InlineData("{\"type\":\"speech.audio.delta\",\"audio\":\"AB==\"}")]
    [InlineData("{\"type\":\"speech.audio.delta\",\"type\":\"speech.audio.done\",\"audio\":\"AP+A\"}")]
    [InlineData("{\"type\":\"future\",\"nested\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"type\":\"future\",\"nested\":[{\"x\":1,\"x\":2}]}")]
    [InlineData("{\"type\":\"speech.audio.done\"}")] [InlineData("{\"type\":\"speech.audio.done\",\"usage\":null}")]
    [InlineData("{\"type\":\"speech.audio.done\",\"usage\":[]}")]
    [InlineData("{\"type\":\"speech.audio.done\",\"usage\":{}}")]
    public async Task InvalidNativeEvent_FailsWithoutSuccess(string json)
    {
        var handler = Handler(SpeechSseFixture.Event(json));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        var error = await Record.ExceptionAsync(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)));
        Assert.IsType<InvalidDataException>(error); Assert.Equal(1, handler.Calls);
    }

    /// <summary>必需用量字段只能是原生非负整数，不把字符串、缺项或小数改成零。</summary>
    [Theory] [InlineData("null")] [InlineData("\"3\"")] [InlineData("-1")] [InlineData("0.5")]
    [InlineData("true")] [InlineData("[]")] [InlineData("{}")] [InlineData("missing")]
    public async Task InvalidDoneUsage_DoesNotComplete(string value)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(SpeechSseFixture.Done);
        var usage = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
        if (value == "missing") usage["usage"]!.AsObject().Remove("input_tokens");
        else usage["usage"]!["input_tokens"] = System.Text.Json.Nodes.JsonNode.Parse(value);
        var handler = Handler(SpeechSseFixture.Event(usage.ToJsonString()));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)));
    }

    /// <summary>外层事件名与 JSON 原生类型冲突时不按任意一层猜测语义。</summary>
    [Fact]
    public async Task ConflictingEventName_IsRejected()
    {
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(Handler(SpeechSseFixture.Event(SpeechSseFixture.Delta, "speech.audio.done"))), SpeechFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)));
    }

    /// <summary>未知新事件保留完整 JSON 和原文，不把其音频字段当成已知 audio.delta。</summary>
    [Fact]
    public async Task UnknownEvent_PreservesNativeDataWithoutGuessingAudio()
    {
        const string unknown = "{  \"type\":\"speech.future\", \"audio\":\"?not-a-delta\", \"future\":[null,false]}";
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(Handler(SpeechSseFixture.Event(unknown) + SpeechSseFixture.Event(SpeechSseFixture.Done))), SpeechFixture.Options);
        var chunks = await SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(2, chunks.Count); Assert.True(chunks[0].Audio.IsEmpty); Assert.Equal(unknown, chunks[0].Data.GetProperty("raw_data").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(unknown), chunks[0].Data.GetProperty("data")));
    }

    /// <summary>显式 SSE 请求不能接受二进制降级响应。</summary>
    [Fact]
    public async Task SseRequest_RejectsBinaryResponseBeforeRead()
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream)));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed);
    }

    /// <summary>不允许替换非法 UTF-8 字节或根据 UTF-16 BOM 切换编码。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidUtf8_FailsWithoutReplacement(bool utf16)
    {
        var valid = SpeechSseFixture.Event("{\"type\":\"future\",\"text\":\"?\"}") + SpeechSseFixture.Event(SpeechSseFixture.Done);
        var bytes = Encoding.UTF8.GetBytes(valid);
        if (utf16) bytes = [255, 254, .. Encoding.Unicode.GetBytes(valid)];
        else bytes[Array.IndexOf(bytes, (byte)'?')] = 255;
        var stream = new SpeechReadStream(bytes);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechSseFixture.Response(stream)));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechSseFixture.Request(), TestContext.Current.CancellationToken)));
    }

    /// <summary>所有协议场景都通过替代 HTTP 正文进入实际供应商实现。</summary>
    private static StubHttpHandler Handler(string wire) => new((_, _) => Task.FromResult(SpeechSseFixture.Response(SpeechSseFixture.Stream(wire))));
}
