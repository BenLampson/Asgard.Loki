using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>验证新图像字段与标注层走原生 HTTP，不丢失二进制文件或响应格式。</summary>
public sealed class Image26WireTests
{
    /// <summary>通过公共编辑路由上传独立标注层，借用的非定位流保持打开。</summary>
    [Fact]
    public async Task MultipartMarkup_SendsSeparateFileAndKeepsItOpen()
    {
        using var image = new EditReadStream([1]);
        using var markup = new EditReadStream([0, 255, 128]);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var parts = await EditMultipartWire.Read(request.Content!, token);
            Assert.Equal(5, parts.GetArrayLength());
            var part = parts[3];
            Assert.Contains(part.GetProperty("headers").EnumerateArray(), header => header.GetString()!.Contains("name=markup; filename=layer.png", StringComparison.Ordinal));
            Assert.Contains(part.GetProperty("headers").EnumerateArray(), header => header.GetString() == "Content-Type: image/png");
            Assert.Equal("AP+A", part.GetProperty("base64").GetString());
            Assert.Equal("high", parts[4].GetProperty("text").GetString());
            return EditFixture.Response();
        });
        await using var sdk = EditFixture.Build(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options));
        var request = EditFixture.Multipart(image, Stream.Null, Stream.Null, "minimal") with
        {
            Markup = new() { Content = markup, FileName = "layer.png", ContentType = "image/png" },
            Fields = [new("complexity", "high")]
        };
        await sdk.Image.EditMultipartAsync(request, TestContext.Current.CancellationToken);
        Assert.True(markup.Eof);
        Assert.False(markup.Disposed);
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => sdk.Image.EditMultipartAsync(
            request with { Markup = request.Images[0] }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sdk.Image.EditMultipartAsync(
            request with { Mask = new() { Content = Stream.Null, FileName = "mask.png" } }, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>JSON 编辑保留原生标注引用，但拒绝同时发送遮罩。</summary>
    [Fact]
    public async Task JsonMarkup_PreservesReferenceAndRejectsMaskConflict()
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            var data = document.RootElement;
            Assert.Equal("file-markup", data.GetProperty("markup").GetProperty("file_id").GetString());
            Assert.False(data.TryGetProperty("mask", out _));
            return EditFixture.Response();
        });
        var adapter = new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options);
        var request = EditFixture.Request();
        request.Parameters.Remove("mask");
        request.Parameters["markup"] = JsonSerializer.SerializeToElement(new { file_id = "file-markup" });
        var ct = TestContext.Current.CancellationToken;
        await adapter.EditAsync(request, ct);
        request.Parameters["mask"] = JsonSerializer.SerializeToElement(new { file_id = "file-mask" });
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.EditAsync(request, ct));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生 SVG 与 GIF 请求分别保留正确的参数和完整输出字节。</summary>
    [Theory]
    [InlineData("svg", "{\"model\":\"svg-author\",\"reasoning_effort\":\"high\"}", "<svg/>")]
    [InlineData("gif", null, "GIF89a")]
    public async Task Generation_PreservesNativeOutputSettings(string format, string? svg, string output)
    {
        var bytes = Encoding.UTF8.GetBytes(output);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            var data = document.RootElement;
            Assert.Equal(format, data.GetProperty("output_format").GetString());
            Assert.Equal("high", data.GetProperty("complexity").GetString());
            if (svg is not null)
                Assert.Equal("svg-author", data.GetProperty("svg").GetProperty("model").GetString());
            else
            {
                Assert.Equal(9, data.GetProperty("frame_count").GetInt32());
                Assert.Equal(200, data.GetProperty("frame_duration_ms").GetInt32());
            }
            var response = JsonSerializer.Serialize(new { output_format = format, data = new[] { new { b64_json = Convert.ToBase64String(bytes) } } });
            return EditFixture.Response(new EditReadStream(Encoding.UTF8.GetBytes(response)));
        });
        var adapter = new OpenAiImageAdapter(new StubHttpClientFactory(handler), EditFixture.Options);
        var request = new ImageGenerationRequest { Model = "pictures", Prompt = "native" };
        request.Parameters["output_format"] = JsonSerializer.SerializeToElement(format);
        request.Parameters["complexity"] = JsonSerializer.SerializeToElement("high");
        if (svg is not null) request.Parameters["svg"] = JsonSerializer.Deserialize<JsonElement>(svg);
        else
        {
            request.Parameters["frame_count"] = JsonSerializer.SerializeToElement(9);
            request.Parameters["frame_duration_ms"] = JsonSerializer.SerializeToElement(200);
        }
        var result = await adapter.GenerateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, result.Images[0].Decode().ToArray());
        Assert.Equal(format, result.Data.GetProperty("output_format").GetString());
    }
}
