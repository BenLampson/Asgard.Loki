using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>验证音频流程独立于 LLM，且必须明确完成。</summary>
public sealed class AudioClientTests
{
    /// <summary>音频字节按原始顺序聚合，保留所有元数据帧。</summary>
    [Fact]
    public async Task GenerateAsync_CombinesAudioAndPreservesFrames()
    {
        var data = JsonSerializer.SerializeToElement(new { usage = new { text_words = 3 } });
        var adapter = new StubSpeechAdapter([new(new byte[] { 1, 2 }, false, data, "stub"), new(new byte[] { 3 }, true, data, "stub")]);
        await using var client = new LokiClientBuilder().AddAudio(new AudioOptions
        { SpeechRoutes = [new SpeechRoute { Model = "voice", Targets = [adapter] }] }).Build();
        var result = await client.Audio.Speech.GenerateAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio.ToArray());
        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(3, result.Frames[1].GetProperty("usage").GetProperty("text_words").GetInt32());
    }
    /// <summary>没有完成标志的音频不能作为完整结果返回。</summary>
    [Fact]
    public async Task GenerateAsync_RejectsMissingCompletion()
    {
        var adapter = new StubSpeechAdapter([new(new byte[] { 1 }, false, JsonSerializer.SerializeToElement(new { }), "stub")]);
        await using var client = new LokiClientBuilder().AddAudio(new AudioOptions
        { SpeechRoutes = [new SpeechRoute { Model = "voice", Targets = [adapter] }] }).Build();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Audio.Speech.GenerateAsync(Request(), TestContext.Current.CancellationToken));
    }
    /// <summary>音频模型必须显式配置，不会借用其他能力的模型。</summary>
    [Fact]
    public async Task GenerateAsync_RejectsUnknownAudioModel()
    {
        var adapter = new StubSpeechAdapter([]);
        await using var client = new LokiClientBuilder().AddAudio(new AudioOptions
        { SpeechRoutes = [new SpeechRoute { Model = "voice", Targets = [adapter] }] }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Audio.Speech.GenerateAsync(Request() with { Model = "missing" }, TestContext.Current.CancellationToken));
    }
    /// <summary>创建最小语音请求。</summary>
    private static SpeechRequest Request() => new() { Model = "voice", Text = "你好", Voice = "speaker" };
}
