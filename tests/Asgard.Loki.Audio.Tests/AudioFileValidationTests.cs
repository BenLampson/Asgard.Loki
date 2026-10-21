using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Audio.Tests;

/// <summary>仅校验能力边界，不查询模型/文件目录或改变供应商参数。</summary>
public sealed class AudioFileValidationTests
{
    /// <summary>失败的本地请求在派发和读取前返回，保留原始流的生命周期。</summary>
    [Theory]
    [InlineData(false, "null")] [InlineData(true, "null")] [InlineData(false, "model")] [InlineData(true, "model")]
    [InlineData(false, "file")] [InlineData(true, "file")] [InlineData(false, "content")] [InlineData(true, "content")]
    [InlineData(false, "closed")] [InlineData(true, "closed")] [InlineData(false, "filename")] [InlineData(true, "filename")]
    [InlineData(false, "file-header")] [InlineData(true, "file-header")] [InlineData(false, "media-header")] [InlineData(true, "media-header")]
    [InlineData(false, "media-empty")] [InlineData(true, "media-empty")] [InlineData(false, "fields")] [InlineData(true, "fields")]
    [InlineData(false, "field-null")] [InlineData(true, "field-null")] [InlineData(false, "name")] [InlineData(true, "name")]
    [InlineData(false, "name-control")] [InlineData(true, "name-control")] [InlineData(false, "value")] [InlineData(true, "value")]
    [InlineData(false, "model-part")] [InlineData(true, "model-part")] [InlineData(false, "file-part")] [InlineData(true, "file-part")]
    [InlineData(false, "unknown-model")] [InlineData(true, "unknown-model")]
    public async Task InvalidRequest_IsRejectedBeforeDispatch(bool translation, string kind)
    {
        using var upload = new ObservedAudioUpload([1]); var request = AudioFileFixture.Request(upload);
        if (kind == "closed") upload.Dispose();
        request = kind switch
        {
            "null" => null!, "model" => request with { Model = " " }, "unknown-model" => request with { Model = "unknown" },
            "file" => request with { File = null! }, "content" => request with { File = request.File with { Content = null! } },
            "filename" => request with { File = request.File with { FileName = " " } }, "file-header" => request with { File = request.File with { FileName = "bad\r\nfile" } },
            "media-header" => request with { File = request.File with { ContentType = "audio/wav\0" } }, "media-empty" => request with { File = request.File with { ContentType = "" } },
            "fields" => request with { Fields = null! }, "field-null" => request with { Fields = [null!] },
            "name" => request with { Fields = [new(" ", "x")] }, "name-control" => request with { Fields = [new("future\r", "x")] },
            "value" => request with { Fields = [new("future", null!)] }, "model-part" => request with { Fields = [new("model", "actual")] },
            "file-part" => request with { Fields = [new("file", "not-the-upload")] }, _ => request
        };
        var adapter = new StubAudioFileAdapter(); await using var client = AudioFileFixture.Build(adapter, translation);
        var error = await Record.ExceptionAsync(() => AudioFileFixture.Create(client, request, translation, TestContext.Current.CancellationToken));
        if (kind == "unknown-model") Assert.IsType<InvalidOperationException>(error); else Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(0, adapter.Transcribes + adapter.Translates); Assert.Equal(0, upload.Reads); Assert.Equal(kind == "closed", upload.Disposed);
    }

    /// <summary>取消优先于派发，转录普通/流式及翻译都不消耗目标序列。</summary>
    [Theory] [InlineData("transcription")] [InlineData("stream")] [InlineData("translation")]
    public async Task PreCanceledRequest_DoesNotDispatch(string operation)
    {
        using var upload = new ObservedAudioUpload([1]); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel(); var adapter = new StubAudioFileAdapter(); var request = AudioFileFixture.Request(upload);
        await using var client = AudioFileFixture.Build(adapter, operation == "translation");
        if (operation == "stream") Assert.Throws<OperationCanceledException>(() => client.Audio.Transcriptions.StreamAsync(request, cancellation.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioFileFixture.Create(client, request, operation == "translation", cancellation.Token));
        Assert.Equal(0, adapter.Streams + adapter.Transcribes + adapter.Translates); Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed);
    }

    /// <summary>可省略文件类型，未知/空表单值按原文保留，上传流不参与 JSON 序列化。</summary>
    [Fact]
    public async Task OpaqueMultipartValues_AreNotNormalized()
    {
        using var upload = new ObservedAudioUpload([1]); var request = AudioFileFixture.Request(upload);
        request = request with { File = request.File with { ContentType = null }, Fields = [new("future[]", "null"), new("future[]", "false"), new("future[]", ""), new("future", "001.00")] };
        var serialized = JsonSerializer.Serialize(request); Assert.DoesNotContain("Content\":", serialized);
        var adapter = new StubAudioFileAdapter(); await using var client = AudioFileFixture.Build(adapter);
        await client.Audio.Transcriptions.CreateAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(adapter.LastRequest!.File.ContentType); Assert.Equal(request.Fields, adapter.LastRequest.Fields);
        Assert.Equal(new[] { "null", "false", "", "001.00" }, adapter.LastRequest.Fields.Select(field => field.Value)); Assert.Equal(0, upload.Reads);
    }

    /// <summary>事件只能承载原生对象，不能把标量/数组误当成有效完成事件。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("1")]
    public void InvalidEventContainer_IsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new AudioTranscriptionEvent(document.RootElement, true, new("file", "actual", "provider")));
    }

    /// <summary>返回模型的必要元数据不允许缺失；不要求正文包含通用文本字段。</summary>
    [Fact]
    public void ResultAndEventMetadata_BoundariesAreExplicit()
    {
        var data = JsonSerializer.SerializeToElement(new { future = false });
        Assert.Throws<ArgumentNullException>(() => new AudioTranscriptionEvent(data, true, null!));
        Assert.Throws<ArgumentNullException>(() => new AudioFileResult(ReadOnlyMemory<byte>.Empty, "application/json", null!));
        Assert.Throws<ArgumentException>(() => new AudioFileResult(ReadOnlyMemory<byte>.Empty, " ", new("file", "actual", "provider")));
        Assert.True(new AudioFileResult(ReadOnlyMemory<byte>.Empty, "application/future", new("file", "actual", "provider")).Body.IsEmpty);
    }
}
