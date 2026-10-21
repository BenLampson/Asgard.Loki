using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Volcengine.Tests;

/// <summary>Visual 同步原生请求、独立签名、图片数组和完整结果保真。</summary>
public sealed class JimengImageProtocolTests
{
    /// <summary>真实请求字节与独立样本完全相同，永久和临时凭据均使用固定原生签名。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task ExactRequestAndIndependentSignature(int vectorIndex)
    {
        var vector = JimengFixture.Part("signatures")[vectorIndex]; var token = vector.GetProperty("security_token").GetString();
        using var factory = JimengFixture.Factory(); factory.ConfigureClient = client =>
        {
            client.DefaultRequestHeaders.Add("X-Security-Token", "unrelated-token"); client.DefaultRequestHeaders.Add("X-Date", "unrelated-date");
            client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); client.DefaultRequestHeaders.Add("api-key", "unrelated-key");
        };
        factory.OnSend = async (message, cancellation) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(JimengFixture.Part("url").GetString(), message.RequestUri!.AbsoluteUri);
            Assert.Equal(JimengFixture.Part("request_utf8").GetString(), await message.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal(vector.GetProperty("authorization").GetString(), Assert.Single(message.Headers.GetValues("Authorization")));
            foreach (var header in vector.GetProperty("headers").EnumerateObject())
                Assert.Equal(header.Value.GetString(), Assert.Single(header.Name == "content-type" ? message.Content.Headers.GetValues(header.Name) : message.Headers.GetValues(header.Name)));
            Assert.Equal(token is not null, message.Headers.Contains("X-Security-Token")); Assert.False(message.Headers.Contains("api-key"));
            return JimengFixture.Json(JimengFixture.Part("url_result"));
        };
        var result = await JimengFixture.Adapter(factory, JimengFixture.Options(token)).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(JimengFixture.Part("url_result"), result.Data)); Assert.Equal(2, result.Images.Count);
        Assert.Equal(new ImageResultMetadata("pictures", "jimeng_high_aes_general_v21_L", "jimeng-test"), result.Metadata);
        Assert.Equal("jimeng-image-test", factory.ClientName); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
        Assert.Equal("9007199254740993", result.Data.GetProperty("future").GetProperty("exact").GetRawText()); Assert.False(result.Data.TryGetProperty("created", out _));
    }
    /// <summary>不注入返回格式、尺寸、图片数量或布尔默认值。</summary>
    [Fact]
    public async Task MinimalBodyHasNoConvenienceDefaults()
    {
        using var factory = JimengFixture.Factory(); factory.OnSend = async (message, cancellation) =>
        {
            var body = JimengFixture.Element(await message.Content!.ReadAsStringAsync(cancellation)); Assert.Equal(2, body.EnumerateObject().Count());
            Assert.Equal("jimeng_high_aes_general_v21_L", body.GetProperty("req_key").GetString()); Assert.Equal(JimengFixture.Request().Prompt, body.GetProperty("prompt").GetString());
            return JimengFixture.Json(JimengFixture.Part("url_result"));
        };
        await JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(true), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>两个数组是独立载荷；不按长度合并、配对、补齐或丢弃任一项。</summary>
    [Theory]
    [InlineData("base64_result", 2)] [InlineData("mixed_result", 3)] [InlineData("empty_result", 0)]
    public async Task NativeArraysAndMetadataRemainWhole(string name, int count)
    {
        using var factory = JimengFixture.Factory(name);
        var result = await JimengFixture.Adapter(factory).GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(JimengFixture.Part(name), result.Data)); Assert.Equal(count, result.Images.Count);
        if (count == 0) return;
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Images[0].Decode().ToArray()); Assert.Equal(JsonValueKind.String, result.Images[0].Data.ValueKind);
        if (name == "mixed_result") { Assert.Null(result.Images[0].Url); Assert.Null(result.Images[1].Base64); Assert.Equal("https://result.example.invalid/b.png", result.Images[2].Url); }
    }
    /// <summary>原生 URL、修订提示、请求标识和用量扩展在公共 Image 路由中保持。</summary>
    [Fact]
    public async Task PublicImageRouteUsesIndependentNativeTarget()
    {
        using var factory = JimengFixture.Factory(); await using var client = new LokiClientBuilder().AddImage(new()
        { Routes = [new() { Model = "pictures", Targets = [JimengFixture.Adapter(factory)] }] }).Build();
        var result = await client.Image.GenerateAsync(JimengFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("https://result.example.invalid/a%2Fb.png?sig=A%2BB", result.Images[0].Url); Assert.Equal(1, factory.Calls);
        Assert.Equal("nested-fixture-id", result.Data.GetProperty("data").GetProperty("request_id").GetString());
    }
    /// <summary>调用者后续修改扩展字典不影响已经签名的字节。</summary>
    [Fact]
    public async Task SnapshotPrecedesFactoryAndSigning()
    {
        var request = JimengFixture.Request(); using var factory = JimengFixture.Factory(); factory.OnCreate = request.Parameters.Clear;
        factory.OnSend = async (message, cancellation) =>
        { Assert.Equal(JimengFixture.Part("request_utf8").GetString(), await message.Content!.ReadAsStringAsync(cancellation)); return JimengFixture.Json(JimengFixture.Part("empty_result")); };
        await JimengFixture.Adapter(factory).GenerateAsync(request, TestContext.Current.CancellationToken); Assert.Empty(request.Parameters);
    }
}
