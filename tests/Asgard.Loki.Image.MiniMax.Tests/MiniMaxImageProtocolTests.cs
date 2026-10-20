using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.MiniMax.Tests;

/// <summary>原生模型调用、载荷数组和完整结果的独立离线契约。</summary>
public sealed class MiniMaxImageProtocolTests
{
    /// <summary>原生文生图及参考图输入逐字段一致，模型由完整目标绑定。</summary>
    [Theory]
    [InlineData(0, "url")] [InlineData(1, "base64")]
    public async Task FullNativeRequestAndResultMatchIndependentVectors(int index, string response)
    {
        using var factory = new MiniMaxTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://minimax-images.example.invalid/prefix/v1/image_generation", message.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-minimax-key", message.Headers.Authorization!.Parameter); Assert.Equal("Bearer", message.Headers.Authorization.Scheme); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonElement.DeepEquals(MiniMaxFixture.Part("requests")[index], MiniMaxFixture.Element(await message.Content.ReadAsStringAsync(token)))); return MiniMaxFixture.Json(MiniMaxFixture.Part(response));
        } };
        var result = await new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MiniMaxFixture.Part(response), result.Data));
        Assert.Equal(new ImageResultMetadata("pictures", "image-fixture", "minimax-test"), result.Metadata);
        if (index == 0) { Assert.Equal(2, result.Images.Count); Assert.Equal("https://result.example.invalid/a%2Fb.png?sig=A%2BB", result.Images[0].Url); Assert.Equal(JsonValueKind.String, result.Images[0].Data.ValueKind); Assert.Equal("9007199254740993", result.Data.GetProperty("metadata").GetProperty("future").GetRawText()); }
        else { var image = Assert.Single(result.Images); Assert.Null(image.Url); Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray()); }
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>原生两个数组保留为各自独立视图，不能猜测或合并关联。</summary>
    [Fact]
    public async Task DualCarriersKeepIndependentItemsAndRawJson()
    {
        using var factory = new MiniMaxTestFactory { OnSend = (_, _) => Task.FromResult(MiniMaxFixture.Json(MiniMaxFixture.Part("mixed"))) };
        var result = await new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Images.Count); Assert.Equal("https://result.example.invalid/original.png", result.Images[0].Url); Assert.Null(result.Images[0].Base64);
        Assert.Null(result.Images[1].Url); Assert.Equal("AQID", result.Images[1].Base64); Assert.Equal("BAUG", result.Images[2].Data.GetString()); Assert.True(JsonElement.DeepEquals(MiniMaxFixture.Part("mixed"), result.Data));
    }
    /// <summary>零图片原生失败计数保持原样，不伪造图片或成功数量。</summary>
    [Fact]
    public async Task EmptyNativeOutputKeepsFailureMetadata()
    {
        using var factory = new MiniMaxTestFactory { OnSend = (_, _) => Task.FromResult(MiniMaxFixture.Json(MiniMaxFixture.Part("empty"))) };
        var result = await new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Empty(result.Images); Assert.True(JsonElement.DeepEquals(MiniMaxFixture.Part("empty"), result.Data)); Assert.Equal("2", result.Data.GetProperty("metadata").GetProperty("failed_count").GetString());
    }
    /// <summary>公共 Image 路由也是相同原生请求，不创建第二次转换或传输。</summary>
    [Fact]
    public async Task ExistingPublicRouteUsesNativeWire()
    {
        using var factory = new MiniMaxTestFactory { OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(MiniMaxFixture.Part("requests")[0], MiniMaxFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return MiniMaxFixture.Json(MiniMaxFixture.Part("url")); } };
        await using var client = new LokiClientBuilder().AddImage(new() { Routes = [new() { Model = "pictures", Targets = [new MiniMaxImageAdapter(factory, MiniMaxFixture.Options())] }] }).Build();
        Assert.Equal(2, (await client.Image.GenerateAsync(MiniMaxFixture.Request(), TestContext.Current.CancellationToken)).Images.Count); Assert.Equal(1, factory.Calls);
    }
    /// <summary>请求在创建工厂前冻结，调用者后续更改不能改写发送报文。</summary>
    [Fact]
    public async Task RequestIsFrozenBeforeFactoryCreation()
    {
        var request = MiniMaxFixture.Request(); using var factory = new MiniMaxTestFactory { OnCreate = request.Parameters.Clear, OnSend = async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(MiniMaxFixture.Part("requests")[0], MiniMaxFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return MiniMaxFixture.Json(MiniMaxFixture.Part("url")); } };
        await new MiniMaxImageAdapter(factory, MiniMaxFixture.Options()).GenerateAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
