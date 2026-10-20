using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>兼容配置复用原生终态和所有权规则，不降低校验或伪造完成。</summary>
public sealed class CompatibleAudioLifecycleTests
{
    /// <summary>尾部、释放、取消或长度故障发生前不会交付完成帧。</summary>
    [Theory] [InlineData("read")] [InlineData("dispose")] [InlineData("cancel")] [InlineData("length")] [InlineData("empty")]
    public async Task SpeechTerminalRequiresTransportEnd(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = new SpeechReadStream(kind == "empty" ? [] : CompatibleAudioFixture.Audio) { FailAfterBytes = kind == "read" ? 3 : null,
            FailDispose = kind == "dispose", OnEof = () => { if (kind == "cancel") cancellation.Cancel(); } };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(source, "audio/pcm", kind == "length" ? 9 : kind == "empty" ? 0 : 8)));
        var seen = new List<SpeechChunk>(); var factory = new SpeechTestFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var chunk in new OpenAiSpeechAdapter(factory, CompatibleAudioFixture.SpeechOptions)
                .StreamNativeAsync(CompatibleAudioFixture.Speech(), cancellation.Token)) seen.Add(chunk);
        });
        Assert.All(seen, chunk => Assert.False(chunk.IsCompleted)); Assert.True(source.Disposed); Assert.True(factory.Clients[0].Disposed);
        Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>复用配置不静默放宽 OpenAI 语音字段校验或猜测采样率映射。</summary>
    [Theory] [InlineData("missing-voice")] [InlineData("empty-voice")] [InlineData("sample-rate")]
    public void UnsupportedShapesFailBeforeHttp(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new SpeechTestFactory(handler);
        var adapter = new OpenAiSpeechAdapter(factory, CompatibleAudioFixture.SpeechOptions);
        if (kind == "sample-rate") Assert.Throws<NotSupportedException>(() => adapter.StreamAsync(new() { Model = "voice", Text = "text", Voice = "voice", SampleRate = 16000 }, TestContext.Current.CancellationToken));
        else
        {
            var request = CompatibleAudioFixture.Speech();
            if (kind == "missing-voice") request.Parameters.Remove("voice"); else request.Parameters["voice"] = System.Text.Json.JsonSerializer.SerializeToElement("");
            Assert.Throws<ArgumentException>(() => adapter.StreamNativeAsync(request, TestContext.Current.CancellationToken));
        }
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
