using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Cloudflare.Tests;

/// <summary>原生信封、字段类型和资源失败不能被 HTTP 200 掩盖。</summary>
public sealed class AudioValidationTests
{
    /// <summary>无效状态、结果或未知字段编码均不能产生成功结果。</summary>
    [Theory]
    [InlineData("success-missing")] [InlineData("success-type")]
    [InlineData("errors-missing")] [InlineData("errors-type")]
    [InlineData("result-missing")] [InlineData("result-null")]
    [InlineData("text-missing")] [InlineData("text-type")]
    [InlineData("duplicate")] [InlineData("unicode")]
    [InlineData("truncated")] [InlineData("utf8")]
    [InlineData("response-limit")] [InlineData("length")]
    [InlineData("media-type")] [InlineData("charset")]
    [InlineData("redirect-target")]
    public async Task InvalidResponses(string kind)
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes);
        var root = JsonNode.Parse(AudioFixture.Root.GetProperty("responses").GetProperty("raw").GetRawText())!.AsObject();
        switch (kind)
        {
            case "success-missing": root.Remove("success"); break;
            case "success-type": root["success"] = "true"; break;
            case "errors-missing": root.Remove("errors"); break;
            case "errors-type": root["errors"] = new JsonObject(); break;
            case "result-missing": root.Remove("result"); break;
            case "result-null": root["result"] = null; break;
            case "text-missing": root["result"]!.AsObject().Remove("text"); break;
            case "text-type": root["result"]!["text"] = false; break;
        }
        var json = kind switch { "duplicate" => root.ToJsonString()[..^1] + ",\"Future\":0}",
            "unicode" => root.ToJsonString()[..^1] + ",\"Extra\":\"\\uD800\"}", "truncated" => root.ToJsonString()[..^1], _ => root.ToJsonString() };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = AudioFixture.JsonResponse(json);
            if (kind == "utf8") { response.Content = new AudioResponseContent([0xff, 0xfe]); response.Content.Headers.ContentType = new("application/json"); }
            if (kind == "length") response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json) + 10;
            if (kind == "media-type") response.Content.Headers.ContentType = new("text/event-stream");
            if (kind == "charset") response.Content.Headers.ContentType!.CharSet = "utf-16";
            if (kind == "redirect-target") response.RequestMessage = new(HttpMethod.Post, "https://different.example.invalid/ai/run/other-model");
            return Task.FromResult(response);
        });
        var factory = new AudioTestFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new CloudflareAudioFileAdapter(factory, AudioFixture.Options(response: kind == "response-limit" ? 20 : 16384))
            .TranscribeAsync(AudioFixture.Request(upload), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(upload.Disposed);
    }
    /// <summary>原生失败、矛盾状态和非 JSON HTTP 错误保留原始正文且不重试。</summary>
    [Theory] [InlineData("native")] [InlineData("contradictory")] [InlineData("http")]
    public async Task OriginalErrors(string kind)
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes);
        var node = JsonNode.Parse(AudioFixture.Root.GetProperty("responses").GetProperty("error").GetRawText())!;
        if (kind == "contradictory") node["success"] = true;
        var bytes = kind == "http" ? new byte[] { 0xff, 0, 1, 2 } : Encoding.UTF8.GetBytes(node.ToJsonString());
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
        { Content = new AudioResponseContent(bytes) { Headers = { ContentType = new("application/json") } } }));
        var error = await Assert.ThrowsAsync<AudioFileUpstreamException>(() => new CloudflareAudioFileAdapter(new StubHttpClientFactory(handler), AudioFixture.Options())
            .TranscribeAsync(AudioFixture.Request(upload), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(kind == "http" ? 429 : 200, error.Code); Assert.Equal(1, handler.Calls); Assert.False(upload.Disposed);
    }
    /// <summary>不支持或冲突的参数在工厂、输入读取及网络之前明确失败。</summary>
    [Theory]
    [InlineData("raw-translate")] [InlineData("raw-fields")]
    [InlineData("audio")] [InlineData("Audio")]
    [InlineData("model")] [InlineData("file")]
    [InlineData("task")] [InlineData("Task")]
    [InlineData("stream")] [InlineData("Stream")]
    [InlineData("duplicate")] [InlineData("duplicate-json")]
    [InlineData("unquoted-string")] [InlineData("unicode")]
    [InlineData("invalid-literal")] [InlineData("parameter-limit")]
    [InlineData("file-name")] [InlineData("content-type")]
    public async Task RejectsBeforeFactory(string kind)
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes);
        var fields = kind switch
        {
            "raw-fields" => new[] { new AudioMultipartField("language", "zh") },
            "audio" or "Audio" or "model" or "file" or "Task" or "Stream" => [new(kind, "null")],
            "task" => [new AudioMultipartField("task", "\"translate\"")],
            "stream" => [new AudioMultipartField("stream", "true")],
            "duplicate" => [new AudioMultipartField("language", "\"zh\""), new("language", "\"en\"")],
            "duplicate-json" => [new AudioMultipartField("Future", "{\"x\":1,\"x\":2}")],
            "unquoted-string" => [new AudioMultipartField("language", "zh")],
            "unicode" => [new AudioMultipartField("Future", "\"\\uD800\"")],
            "invalid-literal" => [new AudioMultipartField("Future", "{")],
            _ => Array.Empty<AudioMultipartField>()
        };
        var raw = kind is "raw-fields" or "raw-translate";
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AudioTestFactory(handler);
        var adapter = new CloudflareAudioFileAdapter(factory, AudioFixture.Options(raw ? CloudflareAudioInputFormat.RawBinary : CloudflareAudioInputFormat.JsonBase64, parameters: kind == "parameter-limit" ? 1 : 16384));
        var request = AudioFixture.Request(upload) with { Fields = fields };
        if (kind == "file-name") request = request with { File = request.File with { FileName = "invalid\r\nname" } };
        if (kind == "content-type") request = request with { File = request.File with { ContentType = "invalid type" } };
        await Assert.ThrowsAnyAsync<Exception>(() => kind == "raw-translate" ? adapter.TranslateAsync(request, TestContext.Current.CancellationToken)
            : adapter.TranscribeAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Calls); Assert.Equal(0, handler.Calls); Assert.Equal(0, upload.Consumed); Assert.False(upload.Disposed);
    }
    /// <summary>两种结构的原始音频上限都实际限制上传，拒绝回放且输入仍由调用者拥有。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UploadBudget(bool json)
    {
        using var upload = new AudioUploadProbe(AudioFixture.Bytes) { ChunkSize = 2 };
        using var handler = new StubHttpHandler(async (message, token) =>
        { using var sink = new MemoryStream(); await message.Content!.CopyToAsync(sink, token); return AudioFixture.Response(); });
        var factory = new AudioTestFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new CloudflareAudioFileAdapter(factory, AudioFixture.Options(json ? CloudflareAudioInputFormat.JsonBase64 : CloudflareAudioInputFormat.RawBinary, upload: 3))
            .TranscribeAsync(AudioFixture.Request(upload), TestContext.Current.CancellationToken));
        Assert.InRange(upload.Consumed, 1, 5); Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals);
    }
}
