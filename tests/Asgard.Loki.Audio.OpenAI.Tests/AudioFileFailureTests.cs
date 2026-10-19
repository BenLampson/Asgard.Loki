using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>上传、HTTP、原生 JSON、取消与资源释放失败不能返回伪成功或隐式重新上传。</summary>
public sealed class AudioFileFailureTests
{
    /// <summary>HTTP 错误不依赖正文编码或类型，成功 HTTP 的原生 error 也保留原始失败字节。</summary>
    [Theory] [InlineData(400, "application/json")] [InlineData(401, "text/plain")] [InlineData(429, "application/octet-stream")]
    [InlineData(500, "text/html")] [InlineData(302, null)] [InlineData(200, "application/json")] [InlineData(200, "application/problem+json")]
    public async Task Errors_PreserveStatusContentTypeAndRawBytes(int status, string? media)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = status == 200 ? Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"失败\",\"future\":[null,false]}}") : new byte[] { 255, 0, 128, 13, 10, 42 };
        if (media == "application/problem+json") body = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"failure\"},\"error\":null}");
        var stream = new SpeechReadStream(body); var response = SpeechFixture.Response(stream, media, body.Length, (HttpStatusCode)status);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(response)); var factory = new SpeechTestFactory(handler);
        var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        var error = await Assert.ThrowsAsync<AudioFileUpstreamException>(() => adapter.TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.Equal(status, error.Code); Assert.Equal(body, error.Body.ToArray()); Assert.Equal(media, error.ContentType);
        body[0] = 1; Assert.NotEqual(body[0], error.Body.Span[0]); Assert.True(stream.Disposed);
        Assert.False(upload.Disposed); Assert.Equal(0, upload.Reads); Assert.True(factory.Clients[0].Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生 JSON 必须为不歧义的完整对象，已知格式具有字符串 text。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":1}")] [InlineData("{\"text\":\"x\",\"text\":\"y\"}")]
    [InlineData("{\"text\":\"x\",\"future\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"text\":\"x\",\"future\":[{\"a\":1,\"a\":2}]}")]
    public async Task InvalidJsonShape_DoesNotBecomeSuccess(string json)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var handler = Handler(Encoding.UTF8.GetBytes(json)); var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.False(upload.Disposed);
    }

    /// <summary>无效 UTF-8 或截断 JSON 不通过替换/补全伪装成原生成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidJsonEncodingOrSyntax_IsRejected(bool encoding)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var handler = Handler(encoding ? [255, 0] : Encoding.UTF8.GetBytes("{\"text\":"));
        var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options);
        var error = await Record.ExceptionAsync(() => adapter.TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        if (encoding) Assert.IsType<DecoderFallbackException>(error); else Assert.IsAssignableFrom<System.Text.Json.JsonException>(error);
    }

    /// <summary>成功状态的非预期类型包括 SSE 都在读正文前失败。</summary>
    [Theory] [InlineData(null)] [InlineData("text/event-stream")] [InlineData("text/html")] [InlineData("application/octet-stream")]
    public async Task UnexpectedMedia_FailsBeforeRead(string? media)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes); var stream = new SpeechReadStream([1, 2]);
        var handler = new StubHttpHandler((_, _) => Task.FromResult(SpeechFixture.Response(stream, media, 2)));
        var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed); Assert.False(upload.Disposed);
    }

    /// <summary>声明的原生格式不能被另一响应类型悄悄替代。</summary>
    [Theory] [InlineData("json", "text/plain")] [InlineData("verbose_json", "text/plain")] [InlineData("diarized_json", "text/plain")]
    [InlineData("text", "application/json")] [InlineData("srt", "application/json")] [InlineData("vtt", "application/json")]
    public async Task FormatMismatch_IsExplicit(string format, string media)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var body = Encoding.UTF8.GetBytes(media == "application/json" ? "{\"text\":\"native\"}" : "native");
        var handler = new StubHttpHandler((_, _) => Task.FromResult(AudioFileHttpFixture.Response(body, media)));
        var request = AudioFileHttpFixture.Request(upload, "minimal") with { Fields = [new("response_format", format)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options).TranscribeAsync(request, TestContext.Current.CancellationToken));
    }

    /// <summary>短/长正文、错误长度头、读取/释放失败都不能返回部分结果。</summary>
    [Theory] [InlineData("short")] [InlineData("long")] [InlineData("invalid-length")] [InlineData("read")] [InlineData("dispose")]
    public async Task IncompleteTransport_DoesNotReturnResult(string kind)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes); var bytes = AudioFileHttpFixture.ResponseBytes("rich");
        var stream = new SpeechReadStream(bytes) { FailRead = kind == "read", FailDispose = kind == "dispose" };
        var response = SpeechFixture.Response(stream, "application/json", bytes.Length + (kind == "short" ? 1 : kind == "long" ? -1 : 0));
        if (kind == "invalid-length") { response.Content.Headers.Remove("Content-Length"); response.Content.Headers.TryAddWithoutValidation("Content-Length", "invalid"); }
        var handler = new StubHttpHandler((_, _) => Task.FromResult(response)); var factory = new SpeechTestFactory(handler);
        var error = await Record.ExceptionAsync(() => new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options).TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        if (kind is "read" or "dispose") Assert.IsType<IOException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.True(stream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>取消覆盖派发、发送、上传、下载和 EOF 窗口；不关闭或重新发送原始上传。</summary>
    [Theory] [InlineData("before")] [InlineData("send")] [InlineData("upload")] [InlineData("read")] [InlineData("eof")]
    public async Task Cancellation_ReleasesOwnedResourcesOnly(string phase)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes)
        { BeforeRead = phase == "upload" ? token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } : null };
        var responseStream = new SpeechReadStream(AudioFileHttpFixture.ResponseBytes("rich"))
        {
            BeforeRead = phase == "read" ? token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } : null,
            OnEof = phase == "eof" ? cancellation.Cancel : null
        };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            if (phase == "send") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (phase == "upload") { using var destination = new MemoryStream(); await message.Content!.CopyToAsync(destination, token); }
            return SpeechFixture.Response(responseStream, "application/json", null);
        });
        var factory = new SpeechTestFactory(handler); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        if (phase == "before") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), cancellation.Token));
        Assert.False(upload.Disposed); Assert.All(factory.Clients, client => Assert.True(client.Disposed)); Assert.Equal(phase == "before" ? 0 : 1, handler.Calls);
        if (phase is "read" or "eof") Assert.True(responseStream.Disposed);
    }

    /// <summary>同一上传内容即使被外部处理器要求复制第二次也明确拒绝，不读取空尾部重发。</summary>
    [Fact]
    public async Task Upload_IsSingleSerializationAndBorrowed()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var handler = new StubHttpHandler(async (message, token) =>
        {
            using var first = new MemoryStream(); await message.Content!.CopyToAsync(first, token);
            using var second = new MemoryStream();
            await Assert.ThrowsAsync<InvalidOperationException>(() => message.Content.CopyToAsync(second, token));
            return AudioFileHttpFixture.Response(AudioFileHttpFixture.ResponseBytes("minimal"));
        });
        await new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options).TranscribeAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken);
        Assert.True(upload.Eof); Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>部分上传失败不关闭原始流，不在其他目标重发，传输异常仍然可见。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UploadReadFailure_DoesNotRetryOrFailOver(bool translation)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes) { FailAfterBytes = 3 };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            using var sink = new MemoryStream(); await message.Content!.CopyToAsync(sink, token);
            return AudioFileHttpFixture.Response(AudioFileHttpFixture.ResponseBytes("minimal"));
        });
        var factory = new SpeechTestFactory(handler); var first = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        var second = new OpenAiAudioFileAdapter(factory, new() { Model = "other", ApiKey = "fixture", Id = "file-two" });
        var options = translation ? new AudioOptions { TranslationRoutes = [new() { Model = "recording", Targets = [first, second] }] }
            : new AudioOptions { TranscriptionRoutes = [new() { Model = "recording", Targets = [first, second] }] };
        await using var client = new LokiClientBuilder().AddAudio(options).Build();
        var error = await Record.ExceptionAsync(() => AudioFileHttpFixture.Create(client, AudioFileHttpFixture.Request(upload, "minimal"), translation, TestContext.Current.CancellationToken));
        Assert.IsType<HttpRequestException>(error); Assert.IsType<IOException>(error.InnerException);
        Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients); Assert.True(factory.Clients[0].Disposed);
    }

    /// <summary>正文不被当 UTF-8 文本解码，原生文本编码/空文本/UTF-8 JSON BOM 保持原始字节。</summary>
    [Theory] [InlineData("text-encoding")] [InlineData("empty-text")] [InlineData("json-bom")]
    public async Task NativeBody_IsNotReencoded(string kind)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        byte[] body = kind switch { "text-encoding" => [255, 0, 128], "empty-text" => [], _ => [239, 187, 191, .. Encoding.UTF8.GetBytes("{\"text\":\"你好\"}")] };
        var request = AudioFileHttpFixture.Request(upload, "minimal") with { Fields = [new("response_format", kind == "json-bom" ? "json" : "text")] };
        var handler = new StubHttpHandler((_, _) =>
        {
            var response = AudioFileHttpFixture.Response(body, kind == "json-bom" ? "application/json" : "text/plain");
            if (kind == "text-encoding") response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain; charset=windows-1252");
            return Task.FromResult(response);
        });
        var result = await new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options).TranscribeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(body, result.Body.ToArray()); if (kind == "text-encoding") Assert.Equal("text/plain; charset=windows-1252", result.ContentType);
    }

    /// <summary>替代处理器仅返回本地分片，不访问真实网络或凭据。</summary>
    private static StubHttpHandler Handler(byte[] body) => new((_, _) => Task.FromResult(AudioFileHttpFixture.Response(body)));
}
