using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>直接适配器入口必须在创建 HTTP 客户端和读取上传之前拒绝无效/冲突原生输入。</summary>
public sealed class ImageEditValidationTests
{
    /// <summary>JSON 必需引用、绑定身份和显式传输开关不可被改写为另一种成功调用。</summary>
    [Theory]
    [InlineData("null")] [InlineData("model")] [InlineData("prompt")] [InlineData("parameters")] [InlineData("override-model")] [InlineData("override-prompt")]
    [InlineData("missing-images")] [InlineData("empty-images")] [InlineData("wrong-images")] [InlineData("wrong-ref")] [InlineData("ambiguous-ref")]
    [InlineData("missing-ref")] [InlineData("empty-ref")] [InlineData("wrong-mask")] [InlineData("stream-null")] [InlineData("stream-number")] [InlineData("undefined")]
    public async Task InvalidJson_FailsBeforeHttp(string kind)
    {
        var request = EditFixture.Request();
        switch (kind)
        {
            case "null": request = null!; break;
            case "model": request = request with { Model = "" }; break;
            case "prompt": request = request with { Prompt = " " }; break;
            case "parameters": request = request with { Parameters = null! }; break;
            case "override-model": request.Parameters["model"] = JsonSerializer.SerializeToElement("hidden"); break;
            case "override-prompt": request.Parameters["prompt"] = JsonSerializer.SerializeToElement("hidden"); break;
            case "missing-images": request.Parameters.Remove("images"); break;
            case "undefined": request.Parameters["future"] = default; break;
            default:
                var (key, raw) = kind switch
                {
                    "empty-images" => ("images", "[]"), "wrong-images" => ("images", "{}"), "wrong-ref" => ("images", "[null]"),
                    "ambiguous-ref" => ("images", "[{\"file_id\":\"file\",\"image_url\":\"https://cdn.invalid\"}]"),
                    "missing-ref" => ("images", "[{\"future\":true}]"), "empty-ref" => ("images", "[{\"file_id\":\"\"}]"),
                    "wrong-mask" => ("mask", "[]"), "stream-null" => ("stream", "null"), _ => ("stream", "1")
                };
                request.Parameters[key] = JsonDocument.Parse(raw).RootElement.Clone(); break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not dispatch")); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.EditAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>显式 true 不被静默替换 false，流式能力必须通过后续独立原生接口实现。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeStreamTrue_IsNotDowngraded(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options); var request = EditFixture.Request(); request.Parameters["stream"] = JsonSerializer.SerializeToElement(true);
        if (multipart) await Assert.ThrowsAsync<NotSupportedException>(() => adapter.EditMultipartAsync(EditFixture.Multipart(one, two, mask) with { Fields = [new("stream", "true")] }, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<NotSupportedException>(() => adapter.EditAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.All(new[] { one, two, mask }, stream => { Assert.Equal(0, stream.Reads); Assert.False(stream.Disposed); });
    }
    /// <summary>借用流/遮罩复用和伪造字段不能在派发时变成空上传或歧义表单。</summary>
    [Theory]
    [InlineData("null")] [InlineData("model")] [InlineData("prompt")] [InlineData("images")] [InlineData("empty")] [InlineData("null-image")]
    [InlineData("null-content")] [InlineData("closed")] [InlineData("same-image")] [InlineData("same-mask")] [InlineData("filename")] [InlineData("control")]
    [InlineData("fields")] [InlineData("null-field")] [InlineData("null-value")] [InlineData("field-control")] [InlineData("stream-text")] [InlineData("duplicate-stream")]
    [InlineData("duplicate-format")] [InlineData("model-field")] [InlineData("prompt-field")] [InlineData("image-field")] [InlineData("images-field")] [InlineData("mask-field")]
    public async Task InvalidMultipart_FailsWithoutReading(string kind)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var request = EditFixture.Multipart(one, two, mask); var image = request.Images[0];
        request = kind switch
        {
            "null" => null!, "model" => request with { Model = "" }, "prompt" => request with { Prompt = "" },
            "images" => request with { Images = null! }, "empty" => request with { Images = [] }, "null-image" => request with { Images = [null!] },
            "null-content" => request with { Images = [image with { Content = null! }] }, "closed" => request,
            "same-image" => request with { Images = [image, image] }, "same-mask" => request with { Mask = image },
            "filename" => request with { Images = [image with { FileName = "" }] }, "control" => request with { Mask = request.Mask! with { FileName = "x\r\ninjection" } },
            "fields" => request with { Fields = null! }, "null-field" => request with { Fields = [null!] },
            "null-value" => request with { Fields = [new("future", null!)] }, "field-control" => request with { Fields = [new("x\rinjected", "value")] },
            "stream-text" => request with { Fields = [new("stream", "TRUE")] },
            "duplicate-stream" => request with { Fields = [new("stream", "false"), new("stream", "false")] },
            "duplicate-format" => request with { Fields = [new("output_format", "png"), new("output_format", "webp")] },
            _ => request with { Fields = [new(kind switch { "model-field" => "model", "prompt-field" => "prompt", "image-field" => "image[]", "images-field" => "images", _ => "mask" }, "override")] }
        };
        if (kind == "closed") one.Dispose();
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new OpenAiImageEditAdapter(factory, EditFixture.Options).EditMultipartAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.All(new[] { one, two, mask }, stream => Assert.Equal(0, stream.Reads)); Assert.False(mask.Disposed); Assert.False(two.Disposed);
    }
    /// <summary>已知 MIME/UTF-8 头或提示词不能被有损修复后再上传。</summary>
    [Theory] [InlineData("mime")] [InlineData("prompt")] [InlineData("filename")] [InlineData("field-value")]
    public async Task InvalidEncodingOrMime_FailsLocally(string kind)
    {
        using var image = new EditReadStream([1]); var request = EditFixture.Multipart(image, Stream.Null, Stream.Null, "minimal");
        request = kind switch
        {
            "mime" => request with { Images = [request.Images[0] with { ContentType = "not-a-media-type" }] },
            "prompt" => request with { Prompt = "\ud800" }, "filename" => request with { Images = [request.Images[0] with { FileName = "\ud800" }] },
            _ => request with { Fields = [new("future", "\ud800")] }
        };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var failure = await Record.ExceptionAsync(() => new OpenAiImageEditAdapter(factory, EditFixture.Options).EditMultipartAsync(request, TestContext.Current.CancellationToken));
        if (kind == "mime") Assert.IsType<FormatException>(failure); else Assert.IsType<EncoderFallbackException>(failure);
        Assert.Empty(factory.Clients); Assert.Equal(0, image.Reads); Assert.False(image.Disposed);
    }
    /// <summary>嵌套重复 JSON 不能通过保留末值掩盖原始引用/未知字段。</summary>
    [Theory] [InlineData("images", "[{\"file_id\":\"one\",\"file_id\":\"two\"}]")] [InlineData("future", "{\"nested\":{\"x\":1,\"x\":2}}")]
    public async Task DuplicateJsonMembers_AreRejected(string key, string raw)
    {
        var request = EditFixture.Request(); using var document = JsonDocument.Parse(raw); request.Parameters[key] = document.RootElement;
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiImageEditAdapter(factory, EditFixture.Options).EditAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients);
    }
    /// <summary>预取消不创建客户端、不读取任何图片/遮罩。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PreCancelled_DoesNotDispatch(bool multipart)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var image = new EditReadStream([1]);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => multipart
            ? adapter.EditMultipartAsync(EditFixture.Multipart(image, Stream.Null, Stream.Null, "minimal"), cancellation.Token) : adapter.EditAsync(EditFixture.Request(), cancellation.Token));
        Assert.Empty(factory.Clients); Assert.Equal(0, image.Reads);
    }
}
