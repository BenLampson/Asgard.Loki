using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>核心只保持原生事件/终态，不把任何供应商名称或 EOF 解释成完成。</summary>
public sealed class AudioFileStreamTests
{
    /// <summary>正常事件即时交付，原始完成对象等源枚举及释放成功后才交付一次。</summary>
    [Fact]
    public async Task Completion_IsHeldUntilSourceEndsAndDisposes()
    {
        using var upload = new ObservedAudioUpload([1]); var events = new[] { StubAudioFileAdapter.Event(false), StubAudioFileAdapter.Event(true) };
        var adapter = new StubAudioFileAdapter { Events = events }; await using var client = AudioFileFixture.Build(adapter); var seen = new List<AudioTranscriptionEvent>();
        await foreach (var item in client.Audio.Transcriptions.StreamAsync(AudioFileFixture.Request(upload), TestContext.Current.CancellationToken))
        { Assert.Equal(item.IsCompleted, adapter.IteratorDisposed); seen.Add(item); }
        Assert.Equal(events, seen); Assert.Same(events[1], seen[1]); Assert.False(upload.Disposed); Assert.Equal(0, upload.Reads);
    }

    /// <summary>缺少、重复、晚到及空事件都不能先发布成功。</summary>
    [Theory] [InlineData("empty")] [InlineData("missing")] [InlineData("duplicate")] [InlineData("late")] [InlineData("null")]
    public async Task InvalidSequence_DoesNotPublishSuccess(string kind)
    {
        using var upload = new ObservedAudioUpload([1]); var ordinary = StubAudioFileAdapter.Event(false); var terminal = StubAudioFileAdapter.Event(true);
        var events = kind switch
        { "empty" => Array.Empty<AudioTranscriptionEvent>(), "missing" => [ordinary], "duplicate" => [ordinary, terminal, terminal], "late" => [ordinary, terminal, ordinary], _ => new AudioTranscriptionEvent[] { ordinary, null! } };
        var adapter = new StubAudioFileAdapter { Events = events }; await using var client = AudioFileFixture.Build(adapter); List<AudioTranscriptionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in client.Audio.Transcriptions.StreamAsync(AudioFileFixture.Request(upload), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.True(adapter.IteratorDisposed); Assert.False(upload.Disposed);
    }

    /// <summary>完成后的源错误/取消不能被已有完成事件掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TailFailureOrCancellation_InvalidatesCompletion(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); using var upload = new ObservedAudioUpload([1]);
        var adapter = new StubAudioFileAdapter { TailFailure = cancel ? null : new IOException("fixture tail error"), OnTail = cancel ? cancellation.Cancel : null };
        await using var client = AudioFileFixture.Build(adapter); List<AudioTranscriptionEvent> seen = [];
        var task = Collect(client.Audio.Transcriptions.StreamAsync(AudioFileFixture.Request(upload), cancellation.Token), seen);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.True(adapter.IteratorDisposed); Assert.False(upload.Disposed);
    }

    /// <summary>真正的 DisposeAsync 失败会撤销暂存的完成事件。</summary>
    [Fact]
    public async Task SourceDisposalFailure_WithholdsCompletion()
    {
        using var upload = new ObservedAudioUpload([1]); var adapter = new DisposalFaultAudioFileAdapter();
        await using var client = new LokiClientBuilder().AddAudio(new()
        { TranscriptionRoutes = [new() { Model = "file", Targets = [adapter] }] }).Build(); List<AudioTranscriptionEvent> seen = [];
        await Assert.ThrowsAsync<IOException>(() => Collect(client.Audio.Transcriptions.StreamAsync(AudioFileFixture.Request(upload), TestContext.Current.CancellationToken), seen));
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.False(upload.Disposed);
    }

    /// <summary>混合路由在选择前整体拒绝流式请求，不能过滤非流式目标或消耗选择序列。</summary>
    [Fact]
    public async Task MixedStreamingTargets_FailBeforeSelection()
    {
        using var upload = new ObservedAudioUpload([1]); var streaming = new StubAudioFileAdapter(); var ordinary = new NonStreamingAudioFileAdapter();
        await using var client = new LokiClientBuilder().AddAudio(new()
        { TranscriptionRoutes = [new() { Model = "file", Targets = [streaming, ordinary] }] }).Build(); var request = AudioFileFixture.Request(upload);
        Assert.Throws<NotSupportedException>(() => client.Audio.Transcriptions.StreamAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, streaming.Streams); Assert.Equal(0, ordinary.Calls);
        await client.Audio.Transcriptions.CreateAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, streaming.Transcribes);
    }

    /// <summary>直接准备流时即复制表单元数据，提前停止释放枚举但不关闭借用的上传和目标。</summary>
    [Fact]
    public async Task SnapshotAndEarlyStop_RespectBorrowedResources()
    {
        using var upload = new ObservedAudioUpload([1]); var request = AudioFileFixture.Request(upload); var fields = request.Fields.ToList(); request = request with { Fields = fields };
        var adapter = new StubAudioFileAdapter(); var client = AudioFileFixture.Build(adapter);
        var source = client.Audio.Transcriptions.StreamAsync(request, TestContext.Current.CancellationToken); fields.Clear();
        Assert.Equal(8, adapter.LastRequest!.Fields.Count);
        await using (var iterator = source.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsCompleted); }
        Assert.True(adapter.IteratorDisposed); await client.DisposeAsync(); Assert.False(adapter.Disposed); Assert.False(upload.Disposed); Assert.Equal(0, upload.Reads);
    }

    /// <summary>仅收集已公开的事件，避免测试误把源内部已生成的终态当成公开成功。</summary>
    private static async Task Collect(IAsyncEnumerable<AudioTranscriptionEvent> source, List<AudioTranscriptionEvent> seen)
    { await foreach (var item in source) seen.Add(item); }
}
