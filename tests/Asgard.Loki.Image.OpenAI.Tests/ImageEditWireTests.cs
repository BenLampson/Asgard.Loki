using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>通过公开 SDK 和实际 HTTP 序列化比对独立完整协议样本。</summary>
public sealed class ImageEditWireTests
{
    /// <summary>模型映射只替换公开别名；引用、遮罩、未知数值及完整响应原文保持独立。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Json_PreservesCompleteNativeRequestAndResult(bool explicitFalse)
    {
        var request = EditFixture.Request(); if (explicitFalse) request.Parameters["stream"] = JsonSerializer.SerializeToElement(false);
        EditReadStream? responseStream = null;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            using var actual = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(token));
            var expected = EditFixture.Json("edit-json-wire");
            if (!explicitFalse) Assert.True(JsonElement.DeepEquals(expected, actual.RootElement));
            else
            {
                Assert.False(actual.RootElement.GetProperty("stream").GetBoolean());
                foreach (var property in expected.EnumerateObject()) Assert.True(JsonElement.DeepEquals(property.Value, actual.RootElement.GetProperty(property.Name)));
                Assert.Equal(expected.EnumerateObject().Count() + 1, actual.RootElement.EnumerateObject().Count());
            }
            Assert.Equal("900719925474099312345", actual.RootElement.GetProperty("future").GetProperty("exact").GetRawText());
            responseStream = new(Encoding.UTF8.GetBytes(EditFixture.Text("edit-json-response")));
            return EditFixture.Response(responseStream);
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var result = await client.Image.EditAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(EditFixture.Text("edit-json-response").Trim(), result.Data.GetRawText()); Assert.True(responseStream!.Eof); Assert.True(responseStream.Disposed);
        Assert.Equal(new byte[] { 0, 255, 128 }, result.Images[0].Decode().ToArray()); Assert.Equal("https://cdn.invalid/output.webp", result.Images[1].Url);
        Assert.Equal("原生 revised 😀", result.Images[0].Data.GetProperty("revised_prompt").GetString()); Assert.Equal(new("pictures", "native-image-model", "edit-target-one"), result.Metadata);
        Assert.Equal("pictures", request.Model); Assert.Equal(1, handler.Calls); Assert.Equal("fixture-images", Assert.Single(factory.Names));
        Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>多图/遮罩和单图输入真实序列化，完整比较 part 头、顺序、文件字节和重复/空文本。</summary>
    [Theory] [InlineData("rich")] [InlineData("minimal")] [InlineData("no-mask")]
    public async Task Multipart_MatchesIndependentFullWireGolden(string kind)
    {
        using var first = new EditReadStream([0, 255, 128]); using var second = new EditReadStream([13, 10, 42]); using var mask = new EditReadStream([1, 2, 3, 4]);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message); var actual = await EditMultipartWire.Read(message.Content!, token);
            Assert.True(JsonElement.DeepEquals(EditFixture.Json("edit-multipart-" + kind), actual), actual.GetRawText());
            return EditFixture.Response();
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var result = await client.Image.EditMultipartAsync(EditFixture.Multipart(first, second, mask, kind), TestContext.Current.CancellationToken);
        Assert.Equal(EditFixture.Text("edit-json-response").Trim(), result.Data.GetRawText()); Assert.Equal(1, handler.Calls);
        Assert.True(first.Eof); Assert.Equal(kind == "rich", mask.Eof); Assert.Equal(kind != "minimal", second.Eof);
        Assert.All(new[] { first, second, mask }, stream => Assert.False(stream.Disposed)); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>直接适配器入口在工厂回调可修改调用者容器之前完成 JSON/上传元数据快照。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DirectAdapter_SnapshotsBeforeHttpFactory(bool multipart)
    {
        using var first = new EditReadStream([0, 255, 128]); using var second = new EditReadStream([13, 10, 42]); using var mask = new EditReadStream([1, 2, 3, 4]);
        var json = EditFixture.Request(); var original = EditFixture.Multipart(first, second, mask); var images = original.Images.ToList(); var fields = original.Fields.ToList();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var actual = multipart ? await EditMultipartWire.Read(message.Content!, token)
                : JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)).RootElement.Clone();
            Assert.True(JsonElement.DeepEquals(EditFixture.Json(multipart ? "edit-multipart-rich" : "edit-json-wire"), actual)); return EditFixture.Response();
        });
        using var factory = new EditTestFactory(handler) { OnCreate = () => { json.Parameters.Clear(); images.Clear(); fields.Clear(); } };
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        if (multipart) await adapter.EditMultipartAsync(original with { Images = images, Fields = fields }, TestContext.Current.CancellationToken);
        else await adapter.EditAsync(json, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>已定位文件从当前位置开始，文件名不用于打开文件，遮罩不被重新定位或处理。</summary>
    [Fact]
    public async Task Multipart_ReadsOnlyCurrentPositions()
    {
        using var image = new MemoryStream([9, 0, 255, 128]); image.Position = 1;
        using var mask = new MemoryStream([9, 1, 2, 3, 4]); mask.Position = 1;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var parts = await EditMultipartWire.Read(message.Content!, token);
            Assert.Equal("AP+A", parts[2].GetProperty("base64").GetString()); Assert.Equal("AQIDBA==", parts[3].GetProperty("base64").GetString()); return EditFixture.Response();
        });
        var adapter = new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options);
        await adapter.EditMultipartAsync(new() { Model = "pictures", Prompt = "native", Images = [new() { Content = image, FileName = "not-a-real-path.png" }], Mask = new() { Content = mask, FileName = "mask.png" } }, TestContext.Current.CancellationToken);
        Assert.Equal(image.Length, image.Position); Assert.Equal(mask.Length, mask.Position); Assert.True(image.CanRead); Assert.True(mask.CanRead);
    }
    /// <summary>重复序列化必须显式失败；不能从流尾再次发送为空的图像。</summary>
    [Fact]
    public async Task Multipart_DoesNotReplayUploadWhenHandlerSerializesTwice()
    {
        using var image = new EditReadStream([0, 255, 128]);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var one = new MemoryStream(); await message.Content!.CopyToAsync(one, token);
            using var two = new MemoryStream(); await message.Content.CopyToAsync(two, token); return EditFixture.Response();
        });
        var adapter = new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.EditMultipartAsync(EditFixture.Multipart(image, Stream.Null, Stream.Null, "minimal"), TestContext.Current.CancellationToken));
        Assert.True(image.Eof); Assert.False(image.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生 URL/file_id/data URL 引用和未给遮罩/null 遮罩均原样保留，不执行额外资源请求。</summary>
    [Theory] [InlineData("image_url", "https://cdn.invalid/source.png", false)] [InlineData("file_id", "file-source", false)]
    [InlineData("image_url", "data:image/png;base64,AP+A", true)]
    public async Task Json_MinimalNativeReferencesRemainOpaque(string field, string source, bool nullMask)
    {
        var request = new ImageEditRequest { Model = "pictures", Prompt = "native",
            Parameters = new() { ["images"] = JsonSerializer.SerializeToElement(new[] { new Dictionary<string, string> { [field] = source } }) } };
        if (nullMask) request.Parameters["mask"] = JsonSerializer.SerializeToElement<object?>(null);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(source, body.RootElement.GetProperty("images")[0].GetProperty(field).GetString());
            Assert.Equal(nullMask, body.RootElement.TryGetProperty("mask", out var mask)); if (nullMask) Assert.Equal(JsonValueKind.Null, mask.ValueKind);
            Assert.False(body.RootElement.TryGetProperty("stream", out _)); return EditFixture.Response(media: "application/vnd.fixture+json; charset=\"utf-8\"");
        });
        await new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).EditAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>Unicode 文件名通过标准原生 filename* 传递，不由 SDK 猜测类型或截断名称。</summary>
    [Fact]
    public async Task Multipart_PreservesUnicodeFileName()
    {
        using var image = new EditReadStream([0, 255, 128]); const string name = "图片 😀.png";
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var parts = await EditMultipartWire.Read(message.Content!, token);
            var disposition = ContentDispositionHeaderValue.Parse(parts[2].GetProperty("headers")[0].GetString()![21..]);
            Assert.Equal(name, disposition.FileNameStar); Assert.Equal("AP+A", parts[2].GetProperty("base64").GetString()); return EditFixture.Response();
        });
        await new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).EditMultipartAsync(new()
        { Model = "pictures", Prompt = "native", Images = [new() { Content = image, FileName = name }] }, TestContext.Current.CancellationToken);
        Assert.False(image.Disposed);
    }
    /// <summary>明确独立端点、身份头和正文内容协商，不发出额外 URL 请求。</summary>
    private static void Identity(HttpRequestMessage message)
    {
        Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://images.invalid/tenant/v1/images/edits", message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-image-secret", message.Headers.Authorization.Parameter);
        Assert.Equal("org-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Organization"))); Assert.Equal("project-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Project")));
        Assert.DoesNotContain(message.Headers.Accept, value => value.MediaType == "text/event-stream");
    }
}
