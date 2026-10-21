using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>转录 SSE 必须正常结束并释放；错误、晚到数据和取消不能先暴露成功。</summary>
public sealed class TranscriptionSseLifecycleTests
{
    /// <summary>缺失/重复/晚到终态及不完整尾部均失败，直接调用和公共 SDK 均不会先收到成功。</summary>
    [Theory]
    [InlineData(false, "empty")] [InlineData(true, "empty")] [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")] [InlineData(false, "late")] [InlineData(true, "late")]
    [InlineData(false, "truncated")] [InlineData(true, "truncated")] [InlineData(false, "sentinel")] [InlineData(true, "sentinel")]
    public async Task InvalidSequence_NeverPublishesCompletion(bool direct, string kind)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var prefix = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta); var done = TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        var body = kind switch { "empty" => "", "missing" => prefix, "duplicate" => prefix + done + done,
            "late" => prefix + done + TranscriptionSseFixture.Event("""{"type":"future"}"""),
            "sentinel" => prefix + done + TranscriptionSseFixture.Event("[DONE]"), _ => prefix + done + "data: {" };
        var stream = TranscriptionSseFixture.Stream(body); var factory = new SpeechTestFactory(Handler(stream));
        var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options); await using var client = AudioFileHttpFixture.Build(adapter, false);
        List<AudioTranscriptionEvent> seen = [];
        var error = await Record.ExceptionAsync(async () =>
        {
            var source = direct ? adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken)
                : client.Audio.Transcriptions.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken);
            await foreach (var item in source) seen.Add(item);
        });
        if (kind == "sentinel") Assert.IsAssignableFrom<JsonException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.Equal(kind == "empty" ? 0 : 1, seen.Count);
        Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(upload.Disposed);
    }

    /// <summary>完成后仍要读取真实源尾部，部分读取或异步释放失败会撤销暂存终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ReadOrDisposalFailure_DoesNotReturnSuccess(bool disposal)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var prefix = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta); var body = prefix + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        var prefixLength = Encoding.UTF8.GetByteCount(prefix);
        var stream = new SpeechReadStream(Encoding.UTF8.GetBytes(body))
        { FragmentSize = prefixLength, FailAfterBytes = disposal ? null : prefixLength, FailDispose = disposal };
        var factory = new SpeechTestFactory(Handler(stream)); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        List<AudioTranscriptionEvent> seen = [];
        await Assert.ThrowsAsync<IOException>(async () =>
        { await foreach (var item in adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(upload.Disposed);
    }

    /// <summary>SSE 长度按原始 UTF-8 传输字节校验，不按事件数或文字长度计算。</summary>
    [Theory] [InlineData(-1)] [InlineData(1)] [InlineData(0)]
    public async Task InvalidDeclaredLength_PreventsCompletion(int difference)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes); var body = TranscriptionSseFixture.Wire();
        var stream = TranscriptionSseFixture.Stream(body); var response = TranscriptionSseFixture.Response(stream, Encoding.UTF8.GetByteCount(body) + difference);
        if (difference == 0) { response.Content.Headers.Remove("Content-Length"); response.Content.Headers.TryAddWithoutValidation("Content-Length", "invalid"); }
        var handler = new StubHttpHandler((_, _) => Task.FromResult(response)); List<AudioTranscriptionEvent> seen = [];
        var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.True(stream.Disposed);
    }

    /// <summary>错误事件可在普通事件之后甚至 done 之后到达，仍保留原生正文并阻止成功。</summary>
    [Theory] [InlineData(false, "typed")] [InlineData(true, "typed")] [InlineData(false, "field")]
    [InlineData(true, "field")] [InlineData(false, "name")] [InlineData(true, "name")]
    public async Task NativeError_PreservesRawBodyAndInvalidatesDone(bool afterDone, string kind)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var errorBody = kind switch { "typed" => """{ "type":"error", "message":"失败", "future":[null,false] }""",
            "field" => """{"error":{"message":"失败","future":false}}""", _ => """{"message":"失败"}""" };
        var body = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta)
            + (afterDone ? TranscriptionSseFixture.Event(TranscriptionSseFixture.Done) : "")
            + "id: failed_frame\nretry: 77\n" + TranscriptionSseFixture.Event(errorBody, kind == "name" ? "error" : null);
        var factory = new SpeechTestFactory(Handler(TranscriptionSseFixture.Stream(body))); List<AudioTranscriptionEvent> seen = [];
        var error = await Assert.ThrowsAsync<AudioFileUpstreamException>(async () =>
        { await foreach (var item in new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options).StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Equal(200, error.Code); Assert.Equal(Encoding.UTF8.GetBytes(errorBody), error.Body.ToArray()); Assert.Equal("text/event-stream", error.ContentType);
        Assert.NotNull(error.TranscriptionEvent); Assert.Equal(errorBody, error.TranscriptionEvent.Data.GetRawText());
        Assert.Equal("failed_frame", error.TranscriptionEvent.EventId); Assert.Equal(77, error.TranscriptionEvent.RetryMilliseconds);
        Assert.Equal(kind == "name" ? "error" : null, error.TranscriptionEvent.EventName); Assert.False(error.TranscriptionEvent.IsCompleted);
        Assert.Equal(new AudioResultMetadata("recording", "file-upstream-fixture", "file-target-one"), error.TranscriptionEvent.Metadata);
        Assert.Single(seen); Assert.False(seen[0].IsCompleted); Assert.True(factory.Clients[0].Disposed); Assert.False(upload.Disposed);
    }

    /// <summary>HTTP/JSON 错误不会被当作普通结果降级；成功 JSON 也不能模拟 SSE。</summary>
    [Theory] [InlineData(400, "text/event-stream")] [InlineData(429, "application/json")]
    [InlineData(200, "application/json")] [InlineData(200, "application/problem+json")] [InlineData(200, "success-json")]
    public async Task WrongTransportOrHttpFailure_DoesNotBecomeEvents(int status, string media)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = media == "success-json" ? Encoding.UTF8.GetBytes("""{"text":"native"}""") : Encoding.UTF8.GetBytes("""{"error":{"message":"failed"}}""");
        var response = SpeechFixture.Response(new SpeechReadStream(body), media == "success-json" ? "application/json" : media, body.Length, (HttpStatusCode)status);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(response)); var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options);
        var task = TranscriptionSseFixture.Collect(adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        if (media == "success-json") await Assert.ThrowsAsync<InvalidDataException>(() => task);
        else { var error = await Assert.ThrowsAsync<AudioFileUpstreamException>(() => task); Assert.Equal(status, error.Code); Assert.Equal(body, error.Body.ToArray()); Assert.Null(error.TranscriptionEvent); }
        Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>前置、上传、增量之后与 EOF 窗口取消都不关闭调用者流或公开完成。</summary>
    [Theory] [InlineData("before")] [InlineData("upload")] [InlineData("delta")] [InlineData("eof")]
    public async Task Cancellation_DoesNotPublishSuccess(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes)
        { BeforeRead = phase == "upload" ? token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } : null };
        var prefix = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta); var body = prefix + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        var stream = new SpeechReadStream(Encoding.UTF8.GetBytes(body)) { FragmentSize = Encoding.UTF8.GetByteCount(prefix), OnEof = phase == "eof" ? cancellation.Cancel : null };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            if (phase == "upload") { using var sink = new MemoryStream(); await message.Content!.CopyToAsync(sink, token); }
            return TranscriptionSseFixture.Response(stream);
        });
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        List<AudioTranscriptionEvent> seen = []; if (phase == "before") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var item in adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), cancellation.Token)) { seen.Add(item); if (phase == "delta") cancellation.Cancel(); } });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.False(upload.Disposed); Assert.All(factory.Clients, client => Assert.True(client.Disposed));
        if (phase is "delta" or "eof") Assert.True(stream.Disposed);
    }

    /// <summary>消费者只读取一个增量后停止时，关闭传输，不补发完成或读取尾部。</summary>
    [Fact]
    public async Task EarlyStop_ReleasesTransportWithoutDrainingUploadOrResponse()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var prefix = TranscriptionSseFixture.Event(TranscriptionSseFixture.Delta); var body = prefix + TranscriptionSseFixture.Event(TranscriptionSseFixture.Done);
        var stream = new SpeechReadStream(Encoding.UTF8.GetBytes(body)) { FragmentSize = Encoding.UTF8.GetByteCount(prefix) };
        var factory = new SpeechTestFactory(Handler(stream)); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        await using (var iterator = adapter.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsCompleted); }
        Assert.Equal(1, stream.Reads); Assert.False(stream.Eof); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(upload.Disposed);
    }

    /// <summary>流请求也使用元数据快照，实际开始枚举前改动列表不影响原生表单。</summary>
    [Fact]
    public async Task StreamRequestSnapshot_IsIndependentBeforeEnumeration()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var request = AudioFileHttpFixture.Request(upload, "diarized"); var fields = request.Fields.ToList(); request = request with { Fields = fields };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("transcription-stream-request"), await AudioMultipartWire.Read(message.Content!, token)));
            return TranscriptionSseFixture.Response(TranscriptionSseFixture.Stream(TranscriptionSseFixture.Event(TranscriptionSseFixture.Done)));
        });
        var source = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options).StreamAsync(request, TestContext.Current.CancellationToken);
        fields.Clear(); Assert.True(Assert.Single(await TranscriptionSseFixture.Collect(source)).IsCompleted); Assert.False(upload.Disposed);
    }

    /// <summary>只以替代 HTTP 处理器提供响应流，既不调用真实供应商也不读取凭据。</summary>
    private static StubHttpHandler Handler(SpeechReadStream stream) => new((_, _) => Task.FromResult(TranscriptionSseFixture.Response(stream)));
}
