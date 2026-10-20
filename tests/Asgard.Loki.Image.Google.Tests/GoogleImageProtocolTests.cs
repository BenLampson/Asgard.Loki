using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Google.Tests;

/// <summary>原生图片、历史签名、终态和安全过滤的完整离线契约。</summary>
public sealed class GoogleImageProtocolTests
{
    /// <summary>完整 native 请求/结果与独立样本一致，解码仅由调用者显式执行。</summary>
    [Fact]
    public async Task NativeJsonMatchesFullWireAndPreservesPartSignatures()
    {
        using var factory = new GoogleTestFactory(); factory.OnSend = async (message, token) =>
        {
            Assert.Equal("https://google-images.example.invalid/prefix/v1beta/models/image-fixture-model:generateContent", message.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Null(message.Headers.Authorization);
            Assert.Equal("fixture-google-image-secret", Assert.Single(message.Headers.GetValues("x-goog-api-key")));
            Assert.True(JsonElement.DeepEquals(GoogleFixture.Part("request"), GoogleFixture.Element(await message.Content!.ReadAsStringAsync(token))));
            return GoogleFixture.Json(GoogleFixture.Part("response"));
        };
        var result = await new GoogleImageAdapter(factory, GoogleFixture.Options()).GenerateNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(GoogleFixture.Part("response"), result.Data)); var image = Assert.Single(result.Images);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray()); Assert.Null(image.Url);
        Assert.Equal("signed-image==", image.Data.GetProperty("thoughtSignature").GetString()); Assert.False(image.Data.TryGetProperty("b64_json", out _));
        Assert.Equal("9007199254740993", result.Data.GetProperty("usageMetadata").GetProperty("promptTokenCount").GetRawText());
        Assert.Equal(new ImageResultMetadata("pictures", "image-fixture-model", "google-images"), result.Metadata);
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>公共 prompt 路由使用 native contents，不添加推测的模态默认设置。</summary>
    [Fact]
    public async Task PublicRouteRetainsNativeSettingsWithoutModelInBody()
    {
        using var factory = new GoogleTestFactory(); factory.OnSend = async (message, token) =>
        {
            var data = GoogleFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.False(data.TryGetProperty("model", out _)); Assert.False(data.TryGetProperty("stream", out _));
            Assert.Equal("a cat", data.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.True(JsonElement.DeepEquals(GoogleFixture.Part("request").GetProperty("generationConfig"), data.GetProperty("generationConfig")));
            return GoogleFixture.Json(GoogleFixture.Part("response"));
        };
        await using var client = new LokiClientBuilder().AddImage(new() { Routes = [new() { Model = "pictures", Targets = [new GoogleImageAdapter(factory, GoogleFixture.Options())] }] }).Build();
        Assert.Single((await client.Image.GenerateAsync(new() { Model = "pictures", Prompt = "a cat", Parameters = new() { ["generationConfig"] = GoogleFixture.Part("request").GetProperty("generationConfig") } }, TestContext.Current.CancellationToken)).Images);
    }
    /// <summary>原生安全结果不丢失，也不伪装为供应商 API 异常。</summary>
    [Theory]
    [InlineData("blocked")] [InlineData("filtered")]
    public async Task NativeSafetyResultsRemainAccessibleWithoutImages(string name)
    {
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Json(GoogleFixture.Part(name))) };
        var result = await new GoogleImageAdapter(factory, GoogleFixture.Options()).GenerateNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(GoogleFixture.Part(name), result.Data)); Assert.Empty(result.Images);
    }
    /// <summary>兼容原生 ProtoJSON 图片字段别名，原始 JSON 不作字段重命名。</summary>
    [Fact]
    public async Task NativeSnakeCaseImagePartKeepsOriginalNames()
    {
        var text = GoogleFixture.Part("response").GetRawText().Replace("inlineData", "inline_data").Replace("mimeType", "mime_type"); var data = GoogleFixture.Element(text);
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Json(data)) };
        var result = await new GoogleImageAdapter(factory, GoogleFixture.Options()).GenerateNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("AQID", Assert.Single(result.Images).Base64); Assert.True(JsonElement.DeepEquals(data, result.Data));
    }
    /// <summary>完整原生 SSE 逐帧一致，保留候选关联、累计用量及终态后的元数据。</summary>
    [Fact]
    public async Task NativeSsePreservesEveryFrameAndFreezesRequest()
    {
        using var factory = new GoogleTestFactory(); factory.OnSend = async (message, token) =>
        {
            Assert.Equal("https://google-images.example.invalid/prefix/v1beta/models/image-fixture-model:streamGenerateContent?alt=sse", message.RequestUri!.AbsoluteUri);
            Assert.True(JsonElement.DeepEquals(GoogleFixture.Part("request"), GoogleFixture.Element(await message.Content!.ReadAsStringAsync(token))));
            return GoogleFixture.Sse(GoogleFixture.Bytes("gemini-images.sse"));
        };
        var request = GoogleFixture.Request(); var stream = new GoogleImageAdapter(factory, GoogleFixture.Options()).StreamNativeAsync(request, TestContext.Current.CancellationToken);
        request.Parameters.Clear(); var result = await GoogleFixture.Collect(stream); Assert.Equal(4, result.Count);
        var expected = Encoding.UTF8.GetString(GoogleFixture.Bytes("gemini-images.sse")).Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => GoogleFixture.Element(line[6..].TrimEnd('\r'))).ToArray();
        for (var i = 0; i < expected.Length; i++) Assert.True(JsonElement.DeepEquals(expected[i], result[i].Data));
        Assert.Equal("message", result[3].EventName); Assert.Equal("g-3", result[3].EventId); Assert.Equal(1000, result[3].RetryMilliseconds);
        Assert.Equal(8, result[3].Data.GetProperty("usageMetadata").GetProperty("promptTokenCount").GetInt32());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GoogleFixture.Collect(stream)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>提示词阻断本身为原生终态，随后独立用量帧仍原样保留。</summary>
    [Fact]
    public async Task PromptBlockAndUsageTailRemainNative()
    {
        var text = "data: " + GoogleFixture.Part("blocked").GetRawText().Replace("\r", "").Replace("\n", "") + "\n\ndata: {\"usageMetadata\":{\"promptTokenCount\":5}}\n\n";
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Sse(Encoding.UTF8.GetBytes(text))) };
        var result = await GoogleFixture.Collect(new GoogleImageAdapter(factory, GoogleFixture.Options()).StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken)); Assert.Equal(2, result.Count);
        Assert.True(JsonElement.DeepEquals(GoogleFixture.Part("blocked"), result[0].Data));
    }
    /// <summary>ProtoJSON 可省略值为零的候选 index，不能把缺省零与显式索引混用判为错误。</summary>
    [Fact]
    public async Task NativeDefaultZeroIndexMayBeOmittedBetweenFrames()
    {
        var text = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"zero\"}]}},{\"index\":7,\"content\":{\"parts\":[{\"text\":\"seven\"}]}}]}\n\n"
            + "data: {\"candidates\":[{\"index\":0,\"finishReason\":\"STOP\"},{\"index\":7,\"finishReason\":\"NO_IMAGE\"}]}\n\n";
        using var factory = new GoogleTestFactory { OnSend = (_, _) => Task.FromResult(GoogleFixture.Sse(Encoding.UTF8.GetBytes(text))) };
        var result = await GoogleFixture.Collect(new GoogleImageAdapter(factory, GoogleFixture.Options()).StreamNativeAsync(GoogleFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(2, result.Count); Assert.False(result[0].Data.GetProperty("candidates")[0].TryGetProperty("index", out _));
    }

}
