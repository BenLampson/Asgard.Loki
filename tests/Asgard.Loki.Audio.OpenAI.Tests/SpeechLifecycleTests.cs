using System.Net;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>二进制流必须正常读尽并释放传输资源；失败、取消和提前停止不能伪造终态。</summary>
public sealed class SpeechLifecycleTests
{
    /// <summary>读取复用缓冲区不影响已交付字节；完成块在 EOF 和客户端/流释放后才可见。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Streaming_EmitsBytesBeforeEofAndCompletionAfterDisposal(bool native)
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream)));
        var factory = new SpeechTestFactory(handler); await using var client = SpeechFixture.Build(new(factory, SpeechFixture.Options));
        var source = native ? client.Audio.Speech.StreamNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken)
            : client.Audio.Speech.StreamAsync(SpeechWireTests.Convenience(), TestContext.Current.CancellationToken);
        List<SpeechChunk> received = [];
        await foreach (var chunk in source)
        {
            if (chunk.IsCompleted)
            { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.True(chunk.Audio.IsEmpty); }
            else { Assert.False(stream.Eof); Assert.False(stream.Disposed); Assert.False(factory.Clients[0].Disposed); }
            Assert.Equal("openai-speech-one", chunk.ProviderId); received.Add(chunk);
        }
        Assert.Equal(SpeechFixture.Bytes, received.SelectMany(c => c.Audio.ToArray())); Assert.True(received[^1].IsCompleted);
    }

    /// <summary>短/长/空正文、部分读取失败、EOF 后释放失败都不暴露成功；直接适配器同样如此。</summary>
    [Theory]
    [InlineData(false, "short")] [InlineData(true, "short")] [InlineData(false, "long")] [InlineData(true, "long")]
    [InlineData(false, "empty")] [InlineData(true, "empty")] [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "dispose")] [InlineData(true, "dispose")]
    public async Task TransportFailure_NeverPublishesCompletion(bool direct, string kind)
    {
        var stream = new SpeechReadStream(kind == "empty" ? [] : SpeechFixture.Bytes) { FailRead = kind == "read", FailDispose = kind == "dispose" };
        var length = kind switch { "short" => 9, "long" => 5, "empty" => 0, _ => 8 };
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream, length: length)));
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        await using var client = SpeechFixture.Build(adapter); List<SpeechChunk> seen = [];
        var error = await Record.ExceptionAsync(async () =>
        {
            var source = direct ? adapter.StreamNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken)
                : client.Audio.Speech.StreamNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken);
            await foreach (var chunk in source) seen.Add(chunk);
        });
        if (kind is "read" or "dispose") Assert.IsType<IOException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.All(seen, c => Assert.False(c.IsCompleted));
        Assert.Equal(kind switch { "empty" => 0, "read" or "long" => 1, _ => 3 }, seen.Count);
        Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>完整聚合接口同样不能把部分音频当成功返回。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Generate_RejectsPartialResult(bool native)
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes) { FailRead = true };
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream)));
        await using var client = SpeechFixture.Build(new(new SpeechTestFactory(handler), SpeechFixture.Options));
        if (native) await Assert.ThrowsAsync<IOException>(() => client.Audio.Speech.GenerateNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<IOException>(() => client.Audio.Speech.GenerateAsync(SpeechWireTests.Convenience(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>取消覆盖尚未派发、发送、读取和正常 EOF 后的窗口。</summary>
    [Theory] [InlineData("before")] [InlineData("send")] [InlineData("read")] [InlineData("eof")]
    public async Task Cancellation_NeverPublishesCompletion(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new SpeechReadStream(SpeechFixture.Bytes)
        {
            OnEof = phase == "eof" ? cancellation.Cancel : null,
            BeforeRead = phase == "read" ? token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } : null
        };
        var handler = new StubHttpHandler((_, token) =>
        {
            if (phase == "send") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(SpeechFixture.Response(stream));
        });
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiSpeechAdapter(factory, SpeechFixture.Options);
        if (phase == "before") cancellation.Cancel(); List<SpeechChunk> seen = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var chunk in adapter.StreamNativeAsync(SpeechFixture.Native(), cancellation.Token)) seen.Add(chunk); });
        Assert.All(seen, c => Assert.False(c.IsCompleted)); Assert.Equal(phase == "before" ? 0 : 1, handler.Calls);
        Assert.All(factory.Clients, c => Assert.True(c.Disposed)); if (phase is "read" or "eof") Assert.True(stream.Disposed);
    }

    /// <summary>调用者提前停止时关闭传输，不继续拉取或释放借用的工厂。</summary>
    [Fact]
    public async Task EarlyStop_ReleasesTransportWithoutReadingToEof()
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream)));
        var factory = new SpeechTestFactory(handler); await using var client = SpeechFixture.Build(new(factory, SpeechFixture.Options));
        await using (var iterator = client.Audio.Speech.StreamNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        { Assert.True(await iterator.MoveNextAsync()); Assert.False(iterator.Current.IsCompleted); }
        Assert.Equal(1, stream.Reads); Assert.False(stream.Eof); Assert.True(stream.Disposed);
        Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }

    /// <summary>HTTP 错误和成功状态携带的错误正文保留原文，绝不拼入音频。</summary>
    [Theory] [InlineData(400, "application/json")] [InlineData(401, "text/plain")]
    [InlineData(429, "application/octet-stream")] [InlineData(500, "text/event-stream")]
    [InlineData(200, "application/json")] [InlineData(200, "application/problem+json")] [InlineData(200, "text/html")]
    public async Task ErrorBody_PreservesStatusAndRawText(int status, string media)
    {
        const string body = "{\"error\":{\"message\":\"fixture 你好\",\"future\":[null,false]}}";
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) }));
        var factory = new SpeechTestFactory(handler); await using var client = SpeechFixture.Build(new(factory, SpeechFixture.Options));
        var error = await Assert.ThrowsAsync<SpeechUpstreamException>(() => client.Audio.Speech.GenerateNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.Code); Assert.Equal(body, error.ResponseBody); Assert.Equal(1, handler.Calls); Assert.True(factory.Clients[0].Disposed);
    }

    /// <summary>SSE、非音频、缺少媒体类型或格式冲突在读正文前失败。</summary>
    [Theory] [InlineData("text/event-stream")] [InlineData("application/xml")]
    [InlineData(null)] [InlineData("audio/wav")]
    public async Task UnexpectedContentType_FailsBeforeReading(string? media)
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream, media)));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        var error = await Record.ExceptionAsync(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken)));
        if (media == "text/event-stream") Assert.IsType<NotSupportedException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed);
    }

    /// <summary>非法或重复长度声明不能退化成未知长度后被当作成功。</summary>
    [Theory] [InlineData("garbage")] [InlineData("-1")] [InlineData("8,8")] [InlineData("8,9")]
    public async Task InvalidLengthHeader_FailsBeforeReading(string length)
    {
        var stream = new SpeechReadStream(SpeechFixture.Bytes); var response = SpeechFixture.Response(stream, length: null);
        Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Length", length));
        var handler = new StubHttpHandler((_, _) => Task.FromResult(response));
        var adapter = new OpenAiSpeechAdapter(new SpeechTestFactory(handler), SpeechFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechFixture.Collect(adapter.StreamNativeAsync(SpeechFixture.Native(), TestContext.Current.CancellationToken)));
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed);
    }
}
