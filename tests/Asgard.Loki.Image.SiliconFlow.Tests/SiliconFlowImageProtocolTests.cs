using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.SiliconFlow.Tests;

/// <summary>完整原生 image_size/batch_size/图像输入及 images/timings/seed 结果。</summary>
public sealed class SiliconFlowImageProtocolTests
{
    /// <summary>两个独立原生请求逐字段匹配，无 OpenAI size/n 映射或默认值覆盖。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task FullNativeWireAndResultRemainOriginal(int index)
    {
        using var factory = new SiliconFlowTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://siliconflow-images.example.invalid/prefix/v1/images/generations", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-siliconflow-key", message.Headers.Authorization.Parameter);
            var body = SiliconFlowFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(SiliconFlowFixture.Part("requests")[index], body));
            Assert.False(body.TryGetProperty("size", out _)); Assert.False(body.TryGetProperty("n", out _)); return SiliconFlowFixture.Json(SiliconFlowFixture.Part("response"));
        } };
        var result = await new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(SiliconFlowFixture.Part("response"), result.Data)); Assert.Equal(2, result.Images.Count);
        Assert.Equal("https://result.example.invalid/a%2Fb.png?sig=A%2BB", result.Images[0].Url); Assert.Null(result.Images[0].Base64); Assert.False(result.Images[0].Data.GetProperty("future").GetBoolean());
        Assert.False(result.Data.TryGetProperty("data", out _)); Assert.Equal("9007199254740993", result.Data.GetProperty("timings").GetProperty("future").GetRawText());
        Assert.Equal(0, result.Data.GetProperty("seed").GetInt32()); Assert.Equal(new ImageResultMetadata("pictures", "vendor/image-fixture", "siliconflow-test"), result.Metadata);
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>合法原生空数组和元数据保持原样，不补图片或数量。</summary>
    [Fact]
    public async Task EmptyNativeOutputIsOriginal()
    {
        using var factory = new SiliconFlowTestFactory { OnSend = (_, _) => Task.FromResult(SiliconFlowFixture.Json(SiliconFlowFixture.Part("empty"))) };
        var result = await new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Empty(result.Images); Assert.True(JsonElement.DeepEquals(SiliconFlowFixture.Part("empty"), result.Data));
    }
    /// <summary>既有统一 Image 路由仍保留原生结果封装，而不是返回空的 OpenAI 视图。</summary>
    [Fact]
    public async Task PublicRoutePreservesImagesEnvelope()
    {
        using var factory = new SiliconFlowTestFactory(); await using var client = new LokiClientBuilder().AddImage(new() { Routes = [new() { Model = "pictures", Targets = [new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options())] }] }).Build();
        var result = await client.Image.GenerateAsync(SiliconFlowFixture.Request(), TestContext.Current.CancellationToken); Assert.Equal(2, result.Images.Count); Assert.True(result.Data.TryGetProperty("timings", out _)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>完整请求在工厂及 HTTP 等待前冻结。</summary>
    [Fact]
    public async Task RequestSnapshotPrecedesFactory()
    {
        var request = SiliconFlowFixture.Request(); using var factory = new SiliconFlowTestFactory { OnCreate = request.Parameters.Clear, OnSend = async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(SiliconFlowFixture.Part("requests")[0], SiliconFlowFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return SiliconFlowFixture.Json(SiliconFlowFixture.Part("response")); } };
        await new SiliconFlowImageAdapter(factory, SiliconFlowFixture.Options()).GenerateAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
