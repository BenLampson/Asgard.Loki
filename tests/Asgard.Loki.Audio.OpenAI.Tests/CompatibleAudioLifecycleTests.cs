using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>兼容配置只验证一次部分读取失败，原生边界矩阵由 SpeechLifecycleTests 负责。</summary>
public sealed class CompatibleAudioLifecycleTests
{
    /// <summary>配置自己的根、模型和凭据后，部分音频仍不能变成完成。</summary>
    [Fact]
    public async Task PartialReadFailure_DiscardsCompletion()
    {
        var source = new SpeechReadStream(CompatibleAudioFixture.Audio) { FailAfterBytes = 3 };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(source, "audio/pcm", 8)));
        var factory = new SpeechTestFactory(handler); List<SpeechChunk> seen = [];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var chunk in new OpenAiSpeechAdapter(factory, CompatibleAudioFixture.SpeechOptions)
                .StreamNativeAsync(CompatibleAudioFixture.Speech(), TestContext.Current.CancellationToken)) seen.Add(chunk);
        });
        Assert.All(seen, chunk => Assert.False(chunk.IsCompleted)); Assert.True(source.Disposed);
        Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
}
