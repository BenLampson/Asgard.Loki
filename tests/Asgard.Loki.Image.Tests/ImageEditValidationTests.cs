using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Tests;

/// <summary>原生请求与上传所有权边界在任何完整目标选择之前校验。</summary>
public sealed class ImageEditValidationTests
{
    /// <summary>JSON 原生参数不允许覆盖绑定字段或携带 Undefined，未知模型不借用生成路径。</summary>
    [Theory] [InlineData("null")] [InlineData("model")] [InlineData("prompt")] [InlineData("parameters")]
    [InlineData("model-override")] [InlineData("prompt-override")] [InlineData("undefined")] [InlineData("unknown")]
    public async Task InvalidJsonRequest_FailsBeforeDispatch(string kind)
    {
        var request = ImageEditFixture.Json();
        request = kind switch { "null" => null!, "model" => request with { Model = "" }, "prompt" => request with { Prompt = " " },
            "parameters" => request with { Parameters = null! }, "unknown" => request with { Model = "unknown" }, _ => request };
        if (kind.EndsWith("-override", StringComparison.Ordinal)) request.Parameters[kind.Split('-')[0]] = JsonSerializer.SerializeToElement("override");
        if (kind == "undefined") request.Parameters["future"] = default;
        var target = new StubImageEditAdapter(); await using var client = ImageEditFixture.Build(target);
        var error = await Record.ExceptionAsync(() => client.Image.EditAsync(request, TestContext.Current.CancellationToken));
        if (kind == "unknown") Assert.IsType<InvalidOperationException>(error); else Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(0, target.Edits);
    }

    /// <summary>图片、遮罩和字段的空值/控制字符/流复用都不应静默改写为其他输入。</summary>
    [Theory] [InlineData("null")] [InlineData("model")] [InlineData("prompt")] [InlineData("images")]
    [InlineData("empty")] [InlineData("image-null")] [InlineData("content")] [InlineData("closed")]
    [InlineData("filename")] [InlineData("file-control")] [InlineData("media-empty")] [InlineData("media-control")]
    [InlineData("mask-content")] [InlineData("mask-name")] [InlineData("same-images")] [InlineData("same-mask")]
    [InlineData("fields")] [InlineData("field-null")] [InlineData("field-name")] [InlineData("field-control")] [InlineData("field-value")]
    [InlineData("model-part")] [InlineData("prompt-part")] [InlineData("image-part")] [InlineData("image[]-part")]
    [InlineData("images-part")] [InlineData("mask-part")] [InlineData("unknown")]
    public async Task InvalidMultipart_FailsWithoutReadingOrClosingSources(string kind)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var request = ImageEditFixture.Multipart(one, two, mask); var image = request.Images[0];
        if (kind == "closed") one.Dispose();
        request = kind switch
        {
            "null" => null!, "model" => request with { Model = "" }, "prompt" => request with { Prompt = " " },
            "images" => request with { Images = null! }, "empty" => request with { Images = [] }, "image-null" => request with { Images = [null!] },
            "content" => request with { Images = [image with { Content = null! }] },
            "filename" => request with { Images = [image with { FileName = "" }] },
            "file-control" => request with { Images = [image with { FileName = "bad\r\nname" }] },
            "media-empty" => request with { Images = [image with { ContentType = "" }] },
            "media-control" => request with { Images = [image with { ContentType = "image/png\0" }] },
            "mask-content" => request with { Mask = request.Mask! with { Content = null! } },
            "mask-name" => request with { Mask = request.Mask! with { FileName = "" } },
            "same-images" => request with { Images = [image, image] },
            "same-mask" => request with { Mask = image with { FileName = "mask.png" } },
            "fields" => request with { Fields = null! }, "field-null" => request with { Fields = [null!] },
            "field-name" => request with { Fields = [new(" ", "native")] }, "field-control" => request with { Fields = [new("bad\0name", "native")] },
            "field-value" => request with { Fields = [new("future", null!)] }, "unknown" => request with { Model = "unknown" }, _ => request
        };
        if (kind.EndsWith("-part", StringComparison.Ordinal)) request = request with { Fields = [new(kind[..^5], "override")] };
        var target = new StubImageEditAdapter(); await using var client = ImageEditFixture.Build(target);
        var error = await Record.ExceptionAsync(() => client.Image.EditMultipartAsync(request, TestContext.Current.CancellationToken));
        if (kind == "unknown") Assert.IsType<InvalidOperationException>(error); else Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(0, target.Multiparts); Assert.All(new[] { one, two, mask }, upload => Assert.Equal(0, upload.Reads));
        Assert.Equal(kind == "closed", one.Disposed); Assert.False(two.Disposed); Assert.False(mask.Disposed);
    }

    /// <summary>取消覆盖所有显式编辑/生成入口，未派发的请求不消耗完整目标选择。</summary>
    [Theory] [InlineData("json")] [InlineData("multipart")] [InlineData("json-stream")] [InlineData("multipart-stream")] [InlineData("generate")] [InlineData("generate-stream")]
    public async Task PreCancellation_DoesNotDispatch(string operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel();
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var target = new StubImageEditAdapter();
        await using var client = new LokiClientBuilder().AddImage(new()
        { Routes = [new() { Model = "pictures", Targets = [target] }], EditRoutes = [new() { Model = "pictures", Targets = [target] }] }).Build();
        var token = cancellation.Token;
        if (operation == "json-stream") Assert.Throws<OperationCanceledException>(() => client.Image.StreamEditAsync(ImageEditFixture.Json(), token));
        else if (operation == "multipart-stream") Assert.Throws<OperationCanceledException>(() => client.Image.StreamMultipartEditAsync(ImageEditFixture.Multipart(one, two, mask), token));
        else if (operation == "generate-stream") await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in client.Image.StreamAsync(new() { Model = "pictures", Prompt = "native" }, token)) { } });
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "json" => client.Image.EditAsync(ImageEditFixture.Json(), token),
            "multipart" => client.Image.EditMultipartAsync(ImageEditFixture.Multipart(one, two, mask), token),
            _ => client.Image.GenerateAsync(new() { Model = "pictures", Prompt = "native" }, token)
        });
        Assert.Equal(0, target.Edits + target.Multiparts + target.Streams + target.Generations);
    }

    /// <summary>事件 JSON 与完整结果拥有独立文档快照，不丢失原生 SSE 身份。</summary>
    [Fact]
    public void NativeResultAndEventSnapshots_AreIndependent()
    {
        const string raw = """{ "type":"native.partial", "b64_json":"AQID", "future":[null,false] }""";
        using var document = JsonDocument.Parse(raw); var metadata = new ImageResultMetadata("public", "actual", "target");
        var item = new ImageEditEvent(document.RootElement, false, metadata, "native", "transport", 17);
        var result = new ImageGenerationResult(document.RootElement, metadata); document.Dispose();
        Assert.Equal(raw, item.Data.GetRawText()); Assert.Equal(raw, result.Data.GetRawText()); Assert.Same(metadata, item.Metadata);
        Assert.Equal("native", item.EventName); Assert.Equal("transport", item.EventId); Assert.Equal(17, item.RetryMilliseconds);
        Assert.Throws<ArgumentNullException>(() => new ImageEditEvent(item.Data, false, null!));
    }

    /// <summary>原生事件必须为 JSON 对象，不能因类型名称猜测一个标量是有效终态。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("1")]
    public void InvalidEventContainer_IsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new ImageEditEvent(document.RootElement, true, new("public", "actual", "target")));
    }
}
