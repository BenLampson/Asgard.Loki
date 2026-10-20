using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.XAI.Tests;

/// <summary>完整原生单图/多图编辑请求、来源关联和结果快照的离线契约。</summary>
public sealed class XaiImageEditProtocolTests
{
    /// <summary>单图 URL/data URI 及有序 URL/文件标识混合多图逐字段保持原样。</summary>
    [Theory]
    [InlineData(0, "url")] [InlineData(1, "base64")] [InlineData(2, "url")]
    public async Task CompleteNativeEditWireAndResultStayOriginal(int index, string response)
    {
        using var factory = new XaiTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://xai-edits.example.invalid/prefix/v1/images/edits", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-xai-edit-key", message.Headers.Authorization.Parameter); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            using var json = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(XaiFixture.Part("requests")[index], json.RootElement));
            if (index == 2) { Assert.True(json.RootElement.GetProperty("images")[0].TryGetProperty("url", out _)); Assert.True(json.RootElement.GetProperty("images")[2].TryGetProperty("file_id", out _)); }
            return XaiFixture.Json(XaiFixture.Part(response));
        } };
        var result = await new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(XaiFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(XaiFixture.Part(response), result.Data)); var image = Assert.Single(result.Images);
        Assert.Equal(new ImageResultMetadata("pictures", "native-xai-fixture", "xai-edits"), result.Metadata); Assert.True(image.Data.TryGetProperty("mime_type", out _));
        if (response == "url") { Assert.Equal("https://result.example.invalid/a%2Fb.jpg?sig=A%2BB", image.Url); Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("cost_in_usd_ticks").GetRawText()); }
        else Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray());
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>编辑专用公共路由不需要注册或借用生成目标，仍保留 xAI 单图字段。</summary>
    [Fact]
    public async Task ExistingEditRouteSupportsNativeXaiJson()
    {
        using var factory = new XaiTestFactory { OnSend = async (message, token) => { using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(XaiFixture.Part("requests")[0], document.RootElement)); return XaiFixture.Json(XaiFixture.Part("url")); } };
        await using var client = new LokiClientBuilder().AddImage(new() { EditRoutes = [new() { Model = "pictures", Targets = [new XaiImageEditAdapter(factory, XaiFixture.Options())] }] }).Build();
        Assert.Single((await client.Image.EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken)).Images); Assert.Equal(1, factory.Calls);
    }
    /// <summary>根参数和嵌套来源在创建工厂之前冻结，后续修改不能改变已发请求。</summary>
    [Fact]
    public async Task SnapshotPrecedesFactoryAndAwait()
    {
        var request = XaiFixture.Request(2); using var factory = new XaiTestFactory { OnCreate = request.Parameters.Clear, OnSend = async (message, token) =>
        { using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(XaiFixture.Part("requests")[2], document.RootElement)); return XaiFixture.Json(XaiFixture.Part("url")); } };
        await new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生空数组保持空结果与原始扩展，不伪造图片或计数。</summary>
    [Fact]
    public async Task EmptyNativeDataRetainsOriginalEnvelope()
    {
        var data = XaiFixture.Element("{\"data\":[],\"usage\":{\"future\":null},\"future\":false}");
        using var factory = new XaiTestFactory { OnSend = (_, _) => Task.FromResult(XaiFixture.Json(data)) };
        var result = await new XaiImageEditAdapter(factory, XaiFixture.Options()).EditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken); Assert.Empty(result.Images); Assert.True(JsonElement.DeepEquals(data, result.Data));
    }
    /// <summary>被拒绝的 multipart 不读取、不释放上传流；不支持的流式编辑同样不发送 HTTP。</summary>
    [Fact]
    public async Task UnsupportedTransportsDoNotTouchBorrowedUploadsOrSend()
    {
        using var factory = new XaiTestFactory(); using var upload = new XaiBorrowedStream();
        await using var client = new LokiClientBuilder().AddImage(new() { EditRoutes = [new() { Model = "pictures", Targets = [new XaiImageEditAdapter(factory, XaiFixture.Options())] }] }).Build();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.Image.EditMultipartAsync(new() { Model = "pictures", Prompt = "a cat", Images = [new() { Content = upload, FileName = "image.png" }] }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(async () => { await foreach (var item in client.Image.StreamEditAsync(XaiFixture.Request(), TestContext.Current.CancellationToken)) _ = item; }); Assert.Equal(0, factory.Calls); Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed);
    }
}
