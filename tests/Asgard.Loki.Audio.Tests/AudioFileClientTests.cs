using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>文件转录/翻译保留完整原生表单和结果，并保持各自的完整目标及借用流边界。</summary>
public sealed class AudioFileClientTests
{
    /// <summary>重复字段、精确文本和未知 JSON 字符串独立快照；流无预读、长度查询或回卷。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeFieldsAndResponse_ArePreserved(bool translation)
    {
        using var upload = new ObservedAudioUpload([0, 255, 128, 13, 10]); var request = AudioFileFixture.Request(upload);
        var fields = request.Fields.ToList(); request = request with { Fields = fields }; var expected = fields.ToArray();
        var adapter = new StubAudioFileAdapter { OnRequest = _ => fields.Clear() };
        var client = AudioFileFixture.Build(adapter, translation);
        var result = await AudioFileFixture.Create(client, request, translation, TestContext.Current.CancellationToken);
        Assert.Equal(expected, adapter.LastRequest!.Fields); Assert.NotSame(fields, adapter.LastRequest.Fields);
        Assert.NotSame(request.File, adapter.LastRequest.File); Assert.Same(upload, adapter.LastRequest.File.Content);
        Assert.Equal("录音-fixture.wav", adapter.LastRequest.File.FileName); Assert.Equal("audio/wav", adapter.LastRequest.File.ContentType);
        Assert.Equal(StubAudioFileAdapter.Fixture(translation ? "translation-core.txt" : "transcription-core.json"), result.Body.ToArray());
        Assert.Equal(translation ? "text/vtt; charset=utf-8" : "application/json; charset=utf-8", result.ContentType);
        Assert.Equal(new AudioResultMetadata("file", "native-file-model", adapter.Id), result.Metadata);
        await client.DisposeAsync(); Assert.False(adapter.Disposed); Assert.False(upload.Disposed); Assert.Equal(0, upload.Reads);
        Assert.Equal(1, translation ? adapter.Translates : adapter.Transcribes);
    }

    /// <summary>流从调用者的当前位置使用，不要求起始为零，也不调用 Seek。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UploadPosition_IsNotReset(bool translation)
    {
        using var upload = new MemoryStream([1, 2, 3, 4]); upload.Position = 2;
        var adapter = new StubAudioFileAdapter { OnRequest = snapshot => Assert.Equal(2, snapshot.File.Content.Position) };
        await using var client = AudioFileFixture.Build(adapter, translation);
        await AudioFileFixture.Create(client, AudioFileFixture.Request(upload), translation, TestContext.Current.CancellationToken);
        Assert.Equal(2, upload.Position); Assert.True(upload.CanRead);
    }

    /// <summary>调用失败不会回卷、关闭或在另一个已配置目标重新上传。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AdapterFailure_DoesNotRetryOrFailOver(bool translation)
    {
        using var upload = new ObservedAudioUpload([1, 2]); var first = new StubAudioFileAdapter("first") { Failure = new IOException("fixture upstream failed") };
        var second = new StubAudioFileAdapter("second"); var options = translation
            ? new AudioOptions { TranslationRoutes = [new() { Model = "file", Targets = [first, second] }] }
            : new AudioOptions { TranscriptionRoutes = [new() { Model = "file", Targets = [first, second] }] };
        await using var client = new LokiClientBuilder().AddAudio(options).Build();
        await Assert.ThrowsAsync<IOException>(() => AudioFileFixture.Create(client, AudioFileFixture.Request(upload), translation, TestContext.Current.CancellationToken));
        Assert.Equal(1, first.Transcribes + first.Translates); Assert.Equal(0, second.Transcribes + second.Translates);
        Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed);
    }

    /// <summary>客户端、操作和模型路由分别持有选择序列；相同目标身份不能把状态合并。</summary>
    [Fact]
    public async Task SelectionState_IsIndependentPerClientOperationAndRoute()
    {
        using var upload = new ObservedAudioUpload([1]); var first = new StubAudioFileAdapter("first"); var second = new StubAudioFileAdapter("second");
        var builder = new LokiClientBuilder().AddAudio(new()
        {
            TranscriptionRoutes = [new() { Model = "file", Targets = [first, second] }, new() { Model = "other", Targets = [first, second] }],
            TranslationRoutes = [new() { Model = "file", Targets = [first, second] }]
        });
        await using var one = builder.Build(); await using var two = builder.Build(); var request = AudioFileFixture.Request(upload); var token = TestContext.Current.CancellationToken;
        Assert.Equal("first", (await one.Audio.Transcriptions.CreateAsync(request, token)).Metadata.ProviderId);
        Assert.Equal("first", (await two.Audio.Transcriptions.CreateAsync(request, token)).Metadata.ProviderId);
        Assert.Equal("first", (await one.Audio.Transcriptions.CreateAsync(request with { Model = "other" }, token)).Metadata.ProviderId);
        Assert.Equal("first", (await one.Audio.Translations.CreateAsync(request, token)).Metadata.ProviderId);
        Assert.Equal("second", (await one.Audio.Transcriptions.CreateAsync(request, token)).Metadata.ProviderId);
        Assert.Equal("second", (await one.Audio.Translations.CreateAsync(request, token)).Metadata.ProviderId);
    }

    /// <summary>配置在 AddAudio 时复制，而非延迟到 Build 后才枚举调用者的可变列表。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Registration_SnapshotsTargetsAndRoutes(bool translation)
    {
        using var upload = new ObservedAudioUpload([1]); var target = new StubAudioFileAdapter();
        var transcribeTargets = new List<IAudioTranscriptionAdapter> { target }; var translateTargets = new List<IAudioTranslationAdapter> { target };
        var transcribeRoutes = new List<AudioTranscriptionRoute> { new() { Model = "file", Targets = transcribeTargets } };
        var translateRoutes = new List<AudioTranslationRoute> { new() { Model = "file", Targets = translateTargets } };
        var builder = new LokiClientBuilder().AddAudio(translation ? new AudioOptions { TranslationRoutes = translateRoutes } : new AudioOptions { TranscriptionRoutes = transcribeRoutes });
        transcribeTargets.Clear(); translateTargets.Clear(); transcribeRoutes.Clear(); translateRoutes.Clear();
        await using var client = builder.Build(); await AudioFileFixture.Create(client, AudioFileFixture.Request(upload), translation, TestContext.Current.CancellationToken);
        Assert.Equal(1, target.Transcribes + target.Translates);
    }

    /// <summary>文件操作可独立注册，未配置的入口不隐式使用其他音频目标。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task IndependentRegistration_RejectsUnconfiguredOperations(bool translation)
    {
        await using var client = AudioFileFixture.Build(new(), translation);
        Assert.Throws<InvalidOperationException>(() => client.Audio.Speech);
        if (translation) Assert.Throws<InvalidOperationException>(() => client.Audio.Transcriptions);
        else Assert.Throws<InvalidOperationException>(() => client.Audio.Translations);
    }

    /// <summary>原始结果字节独立复制，事件 JSON 不依赖已释放文档，也不丢失 SSE 身份。</summary>
    [Fact]
    public void ResultsAndEvents_OwnTheirSnapshots()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"你好\",\"unknown\":[null,false]}"); var original = bytes.ToArray();
        var metadata = new AudioResultMetadata("public", "actual", "provider"); var result = new AudioFileResult(bytes, "application/json", metadata);
        Array.Fill(bytes, (byte)0); Assert.Equal(original, result.Body.ToArray());
        const string raw = "{ \"type\":\"fixture.done\", \"segments\":[{\"id\":\"s1\",\"speaker\":\"A\",\"future\":null}] }";
        using var document = JsonDocument.Parse(raw); var item = new AudioTranscriptionEvent(document.RootElement, true, metadata, "native", "id", 42);
        document.Dispose(); Assert.Equal(raw, item.Data.GetRawText()); Assert.True(item.IsCompleted); Assert.Same(metadata, item.Metadata);
        Assert.Equal("native", item.EventName); Assert.Equal("id", item.EventId); Assert.Equal(42, item.RetryMilliseconds);
    }
}
