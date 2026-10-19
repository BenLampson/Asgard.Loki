using System.Text.Json;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>直接适配器调用与 SDK 路由调用都在 HTTP/上传前校验已知边界。</summary>
public sealed class AudioFileValidationTests
{
    /// <summary>非法原生容器、保留字段、输出控制和头部字符在创建客户端前失败。</summary>
    [Theory] [InlineData("null")] [InlineData("model")] [InlineData("file")] [InlineData("content")]
    [InlineData("closed")] [InlineData("filename")] [InlineData("file-control")] [InlineData("media")]
    [InlineData("fields")] [InlineData("field-null")] [InlineData("name")] [InlineData("name-control")]
    [InlineData("value")] [InlineData("model-part")] [InlineData("file-part")] [InlineData("format-empty")]
    [InlineData("format-duplicate")] [InlineData("stream-duplicate")] [InlineData("stream-value")] [InlineData("stream-true")]
    public async Task InvalidDirectRequest_DoesNotReadOrDispatch(string kind)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes); var request = AudioFileHttpFixture.Request(upload, "minimal");
        if (kind == "closed") upload.Dispose();
        request = kind switch
        {
            "null" => null!, "model" => request with { Model = " " }, "file" => request with { File = null! },
            "content" => request with { File = request.File with { Content = null! } },
            "filename" => request with { File = request.File with { FileName = "" } },
            "file-control" => request with { File = request.File with { FileName = "bad\r\nname" } },
            "media" => request with { File = request.File with { ContentType = "not-mime" } },
            "fields" => request with { Fields = null! }, "field-null" => request with { Fields = [null!] },
            "name" => request with { Fields = [new(" ", "x")] }, "name-control" => request with { Fields = [new("future\0", "x")] },
            "value" => request with { Fields = [new("future", null!)] }, "model-part" => request with { Fields = [new("model", "other")] },
            "file-part" => request with { Fields = [new("file", "other")] }, "format-empty" => request with { Fields = [new("response_format", " ")] },
            "format-duplicate" => request with { Fields = [new("response_format", "json"), new("response_format", "text")] },
            "stream-duplicate" => request with { Fields = [new("stream", "false"), new("stream", "false")] },
            "stream-value" => request with { Fields = [new("stream", "False")] },
            "stream-true" => request with { Fields = [new("stream", "true")] }, _ => request
        };
        var factory = Factory(); var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        var error = await Record.ExceptionAsync(() => adapter.TranscribeAsync(request, TestContext.Current.CancellationToken));
        if (kind == "stream-true") Assert.IsType<NotSupportedException>(error);
        else if (kind == "media") Assert.IsType<FormatException>(error);
        else Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Empty(factory.Clients); Assert.Equal(0, upload.Reads); Assert.Equal(kind == "closed", upload.Disposed);
    }

    /// <summary>普通文件目标不能因类型上属于 OpenAI 就获得未实现的转录流式能力。</summary>
    [Fact]
    public async Task StreamingCapability_RemainsExplicitlyUnsupported()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes); var factory = Factory();
        var adapter = new OpenAiAudioFileAdapter(factory, AudioFileHttpFixture.Options);
        await using var client = AudioFileHttpFixture.Build(adapter, false);
        Assert.False((object)adapter is IStreamingAudioTranscriptionAdapter);
        Assert.Throws<NotSupportedException>(() => client.Audio.Transcriptions.StreamAsync(AudioFileHttpFixture.Request(upload, "minimal"), TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients);
    }

    /// <summary>构造目标时不访问 HTTP，明确拒绝不完整目标与含凭据/查询的 API 根地址。</summary>
    [Theory] [InlineData("id")] [InlineData("model")] [InlineData("key")] [InlineData("name")] [InlineData("uri-null")]
    [InlineData("relative")] [InlineData("scheme")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")]
    public void InvalidOptions_AreRejected(string kind)
    {
        var options = new OpenAiAudioFileOptions
        {
            Id = kind == "id" ? "" : "file", Model = kind == "model" ? " " : "actual", ApiKey = kind == "key" ? "" : "fixture",
            HttpClientName = kind == "name" ? "" : "file",
            BaseAddress = kind switch { "uri-null" => null!, "relative" => new("/relative", UriKind.Relative), "scheme" => new("file:///tmp/audio"),
                "userinfo" => new("https://user:pass@audio.invalid/v1"), "query" => new("https://audio.invalid/v1?x=1"), "fragment" => new("https://audio.invalid/v1#x"), _ => new("https://audio.invalid/v1") }
        };
        var factory = Factory();
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new OpenAiAudioFileAdapter(factory, options))); Assert.Empty(factory.Clients);
    }

    /// <summary>认证字段不参与 JSON；错误体及空构造参数具有明确所有权和异常。</summary>
    [Fact]
    public void SecretNullAndErrorOwnership_BoundariesAreExplicit()
    {
        Assert.DoesNotContain("fixture-file-secret", JsonSerializer.Serialize(AudioFileHttpFixture.Options));
        Assert.DoesNotContain("ApiKey", JsonSerializer.Serialize(AudioFileHttpFixture.Options));
        Assert.Throws<ArgumentNullException>(() => new OpenAiAudioFileAdapter(null!, AudioFileHttpFixture.Options));
        Assert.Throws<ArgumentNullException>(() => new OpenAiAudioFileAdapter(Factory(), null!));
        byte[] raw = [255, 0, 128]; var error = new AudioFileUpstreamException("native", 400, raw, null); raw[0] = 1;
        Assert.Equal(new byte[] { 255, 0, 128 }, error.Body.ToArray()); Assert.Null(error.ContentType);
    }

    /// <summary>供应商入口复制表单列表，发送前的外部修改不改变已经绑定的 multipart。</summary>
    [Fact]
    public async Task DirectSnapshot_PrecedesHttpSerialization()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var request = AudioFileHttpFixture.Request(upload, "rich"); var fields = request.Fields.ToList(); request = request with { Fields = fields };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            fields.Clear(); var actual = await AudioMultipartWire.Read(message.Content!, token);
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("file-rich-request"), actual));
            return AudioFileHttpFixture.Response(AudioFileHttpFixture.ResponseBytes("rich"));
        });
        await new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options).TranscribeAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(fields); Assert.False(upload.Disposed);
    }

    /// <summary>UTF-16 中的孤立代理项不能在原生 UTF-8 multipart 中静默替换为其他字符。</summary>
    [Theory] [InlineData("filename")] [InlineData("name")] [InlineData("value")] [InlineData("model")]
    public async Task InvalidUnicode_IsRejectedWithoutReplacement(string field)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var request = AudioFileHttpFixture.Request(upload, "minimal");
        request = field switch
        {
            "filename" => request with { File = request.File with { FileName = "\ud800.wav" } },
            "name" => request with { Fields = [new("\ud800", "native")] },
            "value" => request with { Fields = [new("future", "\ud800")] }, _ => request
        };
        var options = field == "model" ? new OpenAiAudioFileOptions { Model = "\ud800", ApiKey = "fixture" } : AudioFileHttpFixture.Options;
        var factory = Factory();
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new OpenAiAudioFileAdapter(factory, options).TranscribeAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed);
    }

    /// <summary>无请求应到达此处理器；工厂调用计数用于验证本地失败边界。</summary>
    private static SpeechTestFactory Factory() => new(new StubHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP")));
}
