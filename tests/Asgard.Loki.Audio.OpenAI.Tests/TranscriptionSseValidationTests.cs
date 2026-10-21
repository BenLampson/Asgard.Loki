using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>原生事件容器、分段关联、精确数值与可选 metadata 的协议失败回归。</summary>
public sealed class TranscriptionSseValidationTests
{
    /// <summary>已知事件字段不可缺失、换类型、重复或产生不完整分段。</summary>
    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"type\":null}")]
    [InlineData("{\"type\":1}")] [InlineData("{\"type\":\" \"}")]
    [InlineData("{\"type\":\"transcript.text.delta\"}")] [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":null}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":1}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"segment_id\":0}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"segment_id\":\"\"}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":{}}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[1]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"token\":1}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"bytes\":[256]}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"bytes\":[-1]}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"bytes\":[0.5]}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"bytes\":[255.00000000000000000001]}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"bytes\":[1e-400]}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"logprob\":\"-1\"}]}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"delta\":\"x\",\"logprobs\":[{\"logprob\":1e999}]}")]
    [InlineData("{\"type\":\"transcript.text.segment\"}")]
    [InlineData("{\"type\":\"transcript.text.done\"}")] [InlineData("{\"type\":\"transcript.text.done\",\"text\":null}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":1}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"languages\":{}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"languages\":[\"en\"]}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"languages\":[{\"code\":1}]}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":[]}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":null}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":1}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":\"tokens\"}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":\"tokens\",\"input_tokens\":1,\"output_tokens\":0,\"total_tokens\":1,\"input_token_details\":[]}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":\"tokens\",\"input_tokens\":1,\"output_tokens\":0,\"total_tokens\":1,\"input_token_details\":{\"audio_tokens\":-1e-400}}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":\"tokens\",\"input_tokens\":-1,\"output_tokens\":0,\"total_tokens\":0}}")]
    [InlineData("{\"type\":\"transcript.text.done\",\"text\":\"x\",\"usage\":{\"type\":\"tokens\",\"input_tokens\":-1e-999,\"output_tokens\":0,\"total_tokens\":0}}")]
    [InlineData("{\"type\":\"future\",\"unknown\":{\"id\":1,\"id\":2}}")]
    [InlineData("{\"type\":\"transcript.text.delta\",\"type\":\"transcript.text.done\",\"delta\":\"x\"}")]
    public async Task InvalidNativeShape_FailsWithoutCompletion(string json)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes); var seen = new List<AudioTranscriptionEvent>();
        var handler = Handler(TranscriptionSseFixture.Event(json) + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done));
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options), upload, seen));
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.False(upload.Disposed);
    }

    /// <summary>分段时间范围按原始十进制精确比较；不排序、裁剪或修补无效时间。</summary>
    [Theory] [InlineData("-1", "1")] [InlineData("2", "1")] [InlineData("-1e-999", "0")]
    [InlineData("9007199254740993", "9007199254740992")] [InlineData("1.0000000000000000000001", "1")]
    [InlineData("\"0\"", "1")] [InlineData("0", "null")] [InlineData("0", "1e999")]
    public async Task InvalidSegmentRange_IsNotRoundedAway(string start, string end)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var segment = $$"""{"type":"transcript.text.segment","id":"s","start":{{start}},"end":{{end}},"speaker":"A","text":"native"}""";
        var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(Handler(TranscriptionSseFixture.Event(segment))), AudioFileHttpFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(adapter, upload, []));
    }

    /// <summary>唯一身份、闭合引用和禁止重开都独立于 SSE id，不能复用传输身份来补原生关联。</summary>
    [Theory] [InlineData("orphan")] [InlineData("duplicate")] [InlineData("late-delta")] [InlineData("wrong-id")]
    public async Task InvalidSegmentAssociation_RejectsGlobalDone(string kind)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        const string reference = """{"type":"transcript.text.delta","delta":"native","segment_id":"seg_A"}""";
        var body = kind switch
        {
            "orphan" => TranscriptionSseFixture.Event(reference),
            "duplicate" => TranscriptionSseFixture.Event(TranscriptionSseFixture.Segment) + TranscriptionSseFixture.Event(TranscriptionSseFixture.Segment),
            "late-delta" => TranscriptionSseFixture.Event(TranscriptionSseFixture.Segment) + TranscriptionSseFixture.Event(reference),
            _ => TranscriptionSseFixture.Event(reference) + TranscriptionSseFixture.Event(TranscriptionSseFixture.Segment.Replace("seg_A", "seg_B", StringComparison.Ordinal))
        };
        body = "id: seg_A\n" + body + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        List<AudioTranscriptionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(Handler(body)), AudioFileHttpFixture.Options), upload, seen));
        Assert.All(seen, item => Assert.False(item.IsCompleted));
    }

    /// <summary>有界状态同时计入已完成 ID，不因过早遗忘让后续引用/复用绕过校验。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SegmentLimit_DoesNotEvictEarlierIdentities(bool closed)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var prefix = closed ? TranscriptionSseFixture.Event(TranscriptionSseFixture.Segment)
            : TranscriptionSseFixture.Event("""{"type":"transcript.text.delta","delta":"x","segment_id":"seg_A"}""");
        var body = prefix + TranscriptionSseFixture.Event("""{"type":"transcript.text.delta","delta":"y","segment_id":"seg_B"}""");
        var options = new OpenAiAudioFileOptions { Model = "actual", ApiKey = "fixture", MaxTrackedTranscriptionSegments = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(Handler(body)), options), upload, []));
    }

    /// <summary>event 与 type 的冲突不能通过任选其中一个名字继续解释。</summary>
    [Fact]
    public async Task ConflictingEventName_IsRejected()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta, "transcript.text.done");
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(Handler(body)), AudioFileHttpFixture.Options), upload, []));
    }

    /// <summary>严格 UTF-8 不替换错误字节或根据 UTF-16 BOM 切换编码。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidUtf8_IsNotSilentlyReplaced(bool utf16)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = TranscriptionSseFixture.Event("""{"type":"future","text":"?"}""") + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        byte[] bytes = utf16 ? [255, 254, .. Encoding.Unicode.GetBytes(body)] : Encoding.UTF8.GetBytes(body);
        if (!utf16) bytes[Array.IndexOf(bytes, (byte)'?')] = 255;
        var handler = new StubHttpHandler((_, _) => Task.FromResult(TranscriptionSseFixture.Response(new(bytes))));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => Collect(new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options), upload, []));
    }

    /// <summary>流式请求不能覆盖 false、重复 true 或无效原生开关，错误时不上传。</summary>
    [Theory] [InlineData("false")] [InlineData("True")] [InlineData("duplicate")]
    public void StreamControlConflict_FailsBeforeHttp(string mode)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var request = AudioFileHttpFixture.Request(upload, "minimal") with
        { Fields = mode == "duplicate" ? [new("stream", "true"), new("stream", "true")] : [new("stream", mode)] };
        var factory = new SpeechTestFactory(Handler("")); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        Assert.Throws<ArgumentException>(() => adapter.StreamAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.Equal(0, upload.Reads);
    }

    /// <summary>分段状态上限必须明确为正数。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void InvalidStateLimit_IsRejected(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiAudioFileAdapter(new SpeechTestFactory(Handler("")),
            new() { Model = "actual", ApiKey = "fixture", MaxTrackedTranscriptionSegments = limit }));
    }

    /// <summary>采集实际公开事件，失败时不能把内部已构造的 done 当作已经交付。</summary>
    private static async Task Collect(OpenAiAudioFileAdapter adapter, Stream upload, List<AudioTranscriptionEvent> seen)
    { await foreach (var item in adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken)) seen.Add(item); }
    /// <summary>只使用本地替代处理器。</summary>
    private static StubHttpHandler Handler(string body) => new((_, _) => Task.FromResult(TranscriptionSseFixture.Response(TranscriptionSseFixture.Stream(body))));
}
