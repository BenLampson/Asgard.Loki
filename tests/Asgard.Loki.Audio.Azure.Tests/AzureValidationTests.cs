using System.Net;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>固定字段、输入结构和返回格式的显式失败验证。</summary>
public sealed class AzureValidationTests
{
    /// <summary>原生请求缺失/错误类型及大小写别名在工厂和网络前拒绝。</summary>
    [Theory]
    [InlineData("input")] [InlineData("input-type")] [InlineData("voice")] [InlineData("voice-empty")]
    [InlineData("voice-object")] [InlineData("Input")] [InlineData("Voice")] [InlineData("Model")]
    [InlineData("model")] [InlineData("speed")] [InlineData("response_format")]
    [InlineData("sse")] [InlineData("duplicate")] [InlineData("unicode")] [InlineData("limit")]
    public void SpeechInvalidBeforeFactory(string kind)
    {
        var request = AzureFixture.Speech();
        if (kind is "input" or "voice") request.Parameters.Remove(kind);
        else if (kind == "voice-empty") request.Parameters["voice"] = JsonSerializer.SerializeToElement("");
        else if (kind == "voice-object") request.Parameters["voice"] = JsonSerializer.SerializeToElement(new { id = "not-classic-string" });
        else if (kind == "input-type") request.Parameters["input"] = JsonSerializer.SerializeToElement(1);
        else if (kind is "Input" or "Voice" or "Model" or "model") request.Parameters[kind] = JsonSerializer.SerializeToElement("override");
        else if (kind is "speed" or "response_format") request.Parameters[kind] = JsonSerializer.SerializeToElement(new { invalid = true });
        else if (kind == "sse") request.Parameters["stream_format"] = JsonSerializer.SerializeToElement("sse");
        else if (kind is "duplicate" or "unicode")
        {
            using var document = JsonDocument.Parse(kind == "duplicate" ? "{\"x\":1,\"x\":2}" : "{\"x\":\"\\uD800\"}"); request.Parameters["unknown"] = document.RootElement.Clone();
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AzureTestFactory(handler);
        Assert.ThrowsAny<Exception>(() => new AzureSpeechAdapter(factory, AzureFixture.Options(request: kind == "limit" ? 10 : 16384))
            .StreamNativeAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Calls); Assert.Equal(0, handler.Calls);
    }
    /// <summary>原生文件参数不能重写绑定、损坏 part 头或把 SSE 降级为完整结果。</summary>
    [Theory]
    [InlineData("model")] [InlineData("Model")] [InlineData("file")] [InlineData("File")]
    [InlineData("stream")] [InlineData("duplicate")] [InlineData("case")] [InlineData("empty")]
    [InlineData("limit")] [InlineData("header")] [InlineData("unicode")]
    public async Task FileInvalidBeforeReading(string kind)
    {
        using var upload = new AzureReadStream(AzureFixture.Upload);
        var fields = kind switch { "model" or "Model" or "file" or "File" => new[] { new AudioMultipartField(kind, "override") },
            "stream" => [new AudioMultipartField("stream", "true")], "duplicate" => [new AudioMultipartField("response_format", "json"), new("response_format", "text")],
            "case" => [new AudioMultipartField("Response_Format", "json")], "empty" => [new AudioMultipartField("response_format", "")],
            "header" => [new AudioMultipartField("invalid\r\nname", "text")], "unicode" => [new AudioMultipartField("future", "\ud800")],
            _ => new[] { new AudioMultipartField("future", "longer-than-limit") } };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AzureTestFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new AzureAudioFileAdapter(factory, AzureFixture.Options(request: kind == "limit" ? 1 : 16384))
            .TranscribeAsync(AzureFixture.File(upload) with { Fields = fields }, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Calls); Assert.Equal(0, upload.Consumed); Assert.False(upload.Disposed);
    }
    /// <summary>成功响应不能改目标、格式、状态、字符集或把无效 JSON 当作原生文本。</summary>
    [Theory]
    [InlineData("file-json")] [InlineData("file-text")] [InlineData("file-sse")]
    [InlineData("file-duplicate")] [InlineData("file-unicode")]
    [InlineData("speech-sse")] [InlineData("speech-format")] [InlineData("speech-202")]
    [InlineData("redirect")] [InlineData("file-limit")]
    public async Task ProtocolMismatch(string kind)
    {
        var file = kind.StartsWith("file", StringComparison.Ordinal); using var upload = new AzureReadStream(AzureFixture.Upload);
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = kind switch
            {
                "file-json" => AzureFixture.Response("{}"u8.ToArray()),
                "file-text" => AzureFixture.Response("native text"u8.ToArray(), "text/plain"),
                "file-sse" or "speech-sse" => AzureFixture.Response("data: {}\n\n"u8.ToArray(), "text/event-stream"),
                "file-duplicate" => AzureFixture.Response("{\"text\":\"a\",\"text\":\"b\"}"u8.ToArray()),
                "file-unicode" => AzureFixture.Response("{\"text\":\"a\",\"future\":\"\\uD800\"}"u8.ToArray()),
                "file-limit" => AzureFixture.Response(AzureFixture.Result(false)),
                "speech-format" => AzureFixture.Response(AzureFixture.Audio, "audio/mpeg"),
                "speech-202" => AzureFixture.Response(AzureFixture.Audio, "audio/pcm", HttpStatusCode.Accepted),
                _ => AzureFixture.Response(AzureFixture.Audio, "audio/pcm")
            };
            if (kind == "redirect") response.RequestMessage = new(HttpMethod.Post, "https://other.example.invalid/other-model");
            return Task.FromResult(response);
        });
        var factory = new AzureTestFactory(handler); var options = AzureFixture.Options(response: kind == "file-limit" ? 10 : 16384);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (file) await new AzureAudioFileAdapter(factory, options).TranscribeAsync(AzureFixture.File(upload), TestContext.Current.CancellationToken);
            else await AzureFixture.Collect(new AzureSpeechAdapter(factory, options).StreamNativeAsync(AzureFixture.Speech(), TestContext.Current.CancellationToken));
        });
        Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(upload.Disposed);
    }
    /// <summary>非 UTF-8 原始 HTTP 错误保持字节保真，不尝试解码成有损字符串。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BinaryErrorBody(bool file)
    {
        byte[] raw = [0xff, 0xfe, 0, 1]; using var upload = new AzureReadStream(AzureFixture.Upload);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(AzureFixture.Response(raw, "application/octet-stream", HttpStatusCode.Unauthorized)));
        var factory = new AzureTestFactory(handler);
        var error = file ? await Assert.ThrowsAsync<AzureAudioException>(() => new AzureAudioFileAdapter(factory, AzureFixture.Options()).TranscribeAsync(AzureFixture.File(upload), TestContext.Current.CancellationToken))
            : await Assert.ThrowsAsync<AzureAudioException>(() => AzureFixture.Collect(new AzureSpeechAdapter(factory, AzureFixture.Options()).StreamNativeAsync(AzureFixture.Speech(), TestContext.Current.CancellationToken)));
        Assert.Equal(raw, error.Body.ToArray()); Assert.Equal(401, error.StatusCode); Assert.Equal(1, handler.Calls);
    }
}
