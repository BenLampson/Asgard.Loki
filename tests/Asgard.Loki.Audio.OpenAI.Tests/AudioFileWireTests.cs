using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.OpenAI.Tests;

/// <summary>实际 multipart 字节与独立完整样本比较，保留文件、重复字段和原始结果。</summary>
public sealed class AudioFileWireTests
{
    /// <summary>文件操作的完整请求含每个 part 头和正文，唯一动态项为标准随机 boundary。</summary>
    [Theory] [InlineData("minimal", false)] [InlineData("rich", false)] [InlineData("diarized", false)]
    [InlineData("context", false)] [InlineData("translation", true)]
    public async Task CompleteMultipartSnapshot_PreservesNativeWire(string scenario, bool translation)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var bytes = AudioFileHttpFixture.ResponseBytes(scenario);
        var responseStream = new SpeechReadStream(bytes);
        var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://audio.invalid/tenant/v1/audio/" + (translation ? "translations" : "transcriptions"), message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-file-secret", message.Headers.Authorization.Parameter);
            Assert.Equal("org-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Organization")));
            Assert.Equal("project-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Project")));
            Assert.Null(message.Content!.Headers.ContentLength);
            var actual = await AudioMultipartWire.Read(message.Content, token);
            Assert.True(JsonElement.DeepEquals(SpeechFixture.Golden("file-" + scenario + "-request"), actual), actual.GetRawText());
            return SpeechFixture.Response(responseStream, translation ? "text/plain" : "application/json", bytes.Length);
        });
        var factory = new SpeechTestFactory(handler); await using var client = AudioFileHttpFixture.Build(new(factory, AudioFileHttpFixture.Options), translation);
        var result = await AudioFileHttpFixture.Create(client, AudioFileHttpFixture.Request(upload, scenario), translation, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, result.Body.ToArray()); Assert.Equal(translation ? "text/plain" : "application/json", result.ContentType);
        Assert.Equal(new AudioResultMetadata("recording", "file-upstream-fixture", "file-target-one"), result.Metadata);
        Assert.True(upload.Eof); Assert.False(upload.Disposed); Assert.True(responseStream.Disposed); Assert.True(factory.Clients[0].Disposed);
        Assert.False(factory.Disposed); Assert.Equal("Asgard.Loki.Audio.OpenAI.File", Assert.Single(factory.Names)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>已知原生格式保持完整字节；不按模型名删除或修正调用者参数。</summary>
    [Theory] [InlineData(false, "json")] [InlineData(false, "verbose_json")] [InlineData(false, "diarized_json")]
    [InlineData(false, "text")] [InlineData(false, "srt")] [InlineData(false, "vtt")]
    [InlineData(true, "json")] [InlineData(true, "verbose_json")] [InlineData(true, "text")] [InlineData(true, "srt")] [InlineData(true, "vtt")]
    public async Task NativeResultFormats_RemainUnchanged(bool translation, string format)
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var json = format.EndsWith("json", StringComparison.Ordinal);
        var body = json ? AudioFileHttpFixture.ResponseBytes("rich") : Encoding.UTF8.GetBytes("native\r\ntext 😀\r\n");
        var media = json ? "application/json" : format == "vtt" ? "text/vtt" : format == "srt" ? "application/x-subrip" : "text/plain";
        var request = AudioFileHttpFixture.Request(upload, "minimal") with { Fields = [new("response_format", format)] };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            var wire = await AudioMultipartWire.Read(message.Content!, token);
            Assert.Equal(format, wire[2].GetProperty("text").GetString()); return AudioFileHttpFixture.Response(body, media);
        });
        await using var client = AudioFileHttpFixture.Build(new(new SpeechTestFactory(handler), AudioFileHttpFixture.Options), translation);
        var result = await AudioFileHttpFixture.Create(client, request, translation, TestContext.Current.CancellationToken);
        Assert.Equal(body, result.Body.ToArray()); Assert.Equal(media, result.ContentType); Assert.False(upload.Disposed);
    }

    /// <summary>当前位置之后的音频才上传，中文文件名和实际内容类型不被换成 SDK 默认值。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FileMetadataAndPosition_ArePreserved(bool translation)
    {
        using var upload = new MemoryStream([99, 98, .. AudioFileHttpFixture.UploadBytes]); upload.Position = 2;
        var request = AudioFileHttpFixture.Request(upload, "minimal"); request = request with { File = request.File with { FileName = "录音.wav", ContentType = "audio/wav; codec=pcm" } };
        var handler = new StubHttpHandler(async (message, token) =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(message.Content).ToArray(); var disposition = parts[1].Headers.ContentDisposition!;
            Assert.Equal("录音.wav", disposition.FileName); Assert.Equal("录音.wav", disposition.FileNameStar);
            Assert.Equal("audio/wav; codec=pcm", parts[1].Headers.ContentType!.ToString());
            var wire = await AudioMultipartWire.Read(message.Content!, token);
            Assert.Equal(Convert.ToBase64String(AudioFileHttpFixture.UploadBytes), wire[1].GetProperty("base64").GetString());
            return AudioFileHttpFixture.Response(AudioFileHttpFixture.ResponseBytes("minimal"));
        });
        var adapter = new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), AudioFileHttpFixture.Options);
        if (translation) await adapter.TranslateAsync(request, TestContext.Current.CancellationToken); else await adapter.TranscribeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(upload.Length, upload.Position); Assert.True(upload.CanRead);
    }

    /// <summary>默认地址、可选身份省略及未知格式/结果 JSON 扩展都不产生模型目录分支。</summary>
    [Fact]
    public async Task DefaultRootAndUnknownFormat_AreNative()
    {
        using var upload = new SpeechReadStream(AudioFileHttpFixture.UploadBytes);
        var request = AudioFileHttpFixture.Request(upload, "minimal") with { Fields = [new("response_format", "future")] };
        var body = Encoding.UTF8.GetBytes("{\"future\":[null,false]}");
        var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", message.RequestUri!.AbsoluteUri);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            return Task.FromResult(AudioFileHttpFixture.Response(body));
        });
        var result = await new OpenAiAudioFileAdapter(new SpeechTestFactory(handler), new() { Model = "future", ApiKey = "fixture" }).TranscribeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(body, result.Body.ToArray());
    }
}
