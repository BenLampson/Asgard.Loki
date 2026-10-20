using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Zhipu.Tests;

/// <summary>智谱 v4 原生参数、图片字段及审核/时间/用量的独立离线契约。</summary>
public sealed class ZhipuImageProtocolTests
{
    /// <summary>原生参数逐字段保持，URL 不下载，零时间戳和审核信息不重写。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task CompleteNativeWireAndResultAreOriginal(int index)
    {
        using var factory = new ZhipuTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://zhipu-images.example.invalid/prefix/api/paas/v4/images/generations", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-zhipu-image-key", message.Headers.Authorization.Parameter);
            Assert.True(JsonElement.DeepEquals(ZhipuFixture.Part("requests")[index], ZhipuFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return ZhipuFixture.Json(ZhipuFixture.Part("url"));
        } };
        var result = await new ZhipuImageAdapter(factory, ZhipuFixture.Options()).GenerateAsync(ZhipuFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(ZhipuFixture.Part("url"), result.Data)); Assert.Equal("https://result.example.invalid/a%2Fb.png?sig=A%2BB", Assert.Single(result.Images).Url);
        Assert.Equal(0, result.Data.GetProperty("created").GetInt32()); Assert.True(result.Data.TryGetProperty("content_filter", out _)); Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText());
        Assert.Equal(new ImageResultMetadata("pictures", "native-zhipu-fixture", "zhipu-test"), result.Metadata); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>URL/Base64 别名仅投影到便利视图，原始字段名、空值及全部扩展保持不变。</summary>
    [Theory]
    [InlineData("aliases")] [InlineData("base64")] [InlineData("canonical_base64")]
    public async Task OriginalImageCarriersRemainIndependentOfJsonNames(string name)
    {
        using var factory = new ZhipuTestFactory { OnSend = (_, _) => Task.FromResult(ZhipuFixture.Json(ZhipuFixture.Part(name))) };
        var result = await new ZhipuImageAdapter(factory, ZhipuFixture.Options()).GenerateAsync(ZhipuFixture.Request(), TestContext.Current.CancellationToken); var image = Assert.Single(result.Images);
        Assert.True(JsonElement.DeepEquals(ZhipuFixture.Part(name), result.Data)); Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray());
        if (name == "aliases") { Assert.Equal("https://result.example.invalid/alias.png", image.Url); Assert.Equal(JsonValueKind.Null, image.Data.GetProperty("url").ValueKind); Assert.False(image.Data.TryGetProperty("b64_json", out _)); }
        else if (name == "base64") { Assert.Null(image.Url); Assert.False(image.Data.TryGetProperty("url", out _)); Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("created").ValueKind); }
        Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生零图片结果仍保留审核和请求标识，不发明时间戳或图片。</summary>
    [Fact]
    public async Task EmptyResultRetainsFilterMetadataWithoutLocalTimestamp()
    {
        using var factory = new ZhipuTestFactory { OnSend = (_, _) => Task.FromResult(ZhipuFixture.Json(ZhipuFixture.Part("empty"))) };
        var result = await new ZhipuImageAdapter(factory, ZhipuFixture.Options()).GenerateAsync(ZhipuFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Empty(result.Images); Assert.False(result.Data.TryGetProperty("created", out _)); Assert.True(JsonElement.DeepEquals(ZhipuFixture.Part("empty"), result.Data));
    }
    /// <summary>原生审核结果可以省略图片或保留 null，不补出 data 数组，也不丢失过滤正文。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeFilterOnlyEnvelopeIsKeptWhole(bool nullData)
    {
        var data = ZhipuFixture.Element("{" + (nullData ? "\"data\":null," : "") + "\"content_filter\":[{\"role\":\"assistant\",\"level\":1}],\"future\":false}");
        using var factory = new ZhipuTestFactory { OnSend = (_, _) => Task.FromResult(ZhipuFixture.Json(data)) };
        var result = await new ZhipuImageAdapter(factory, ZhipuFixture.Options()).GenerateAsync(ZhipuFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Empty(result.Images); Assert.True(JsonElement.DeepEquals(data, result.Data)); Assert.Equal(nullData, result.Data.TryGetProperty("data", out _));
    }
    /// <summary>原生生成使用现有 Image 路由，实际模型保持完整目标绑定。</summary>
    [Fact]
    public async Task ExistingPublicRoutePreservesNativeModelAndMetadata()
    {
        using var factory = new ZhipuTestFactory(); await using var client = new LokiClientBuilder().AddImage(new() { Routes = [new() { Model = "pictures", Targets = [new ZhipuImageAdapter(factory, ZhipuFixture.Options())] }] }).Build();
        var result = await client.Image.GenerateAsync(ZhipuFixture.Request(), TestContext.Current.CancellationToken); Assert.Single(result.Images); Assert.True(result.Data.TryGetProperty("content_filter", out _)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生参数在第一次工厂/等待前冻结。</summary>
    [Fact]
    public async Task RequestSnapshotPrecedesFactory()
    {
        var request = ZhipuFixture.Request(); using var factory = new ZhipuTestFactory { OnCreate = request.Parameters.Clear, OnSend = async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(ZhipuFixture.Part("requests")[0], ZhipuFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return ZhipuFixture.Json(ZhipuFixture.Part("url")); } };
        await new ZhipuImageAdapter(factory, ZhipuFixture.Options()).GenerateAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
